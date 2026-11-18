using System;
using System.Collections.Generic;
using Acrux.Rendering;
using Acrux.Render3D.Models;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Acrux.Render3D.Blocks
{
    using BlockModel = MeshAsset;

    // One grid of an explicit capture. Highlights map block positions (IMySlimBlock.Position) to a flat color
    // replacing the block's paint; the dictionary is read by the paint threads, so it is never changed once
    // captured (a new one replaces it).
    internal struct BlockGridView
    {
        public IMyCubeGrid Grid;
        public Dictionary<Vector3I, Vector3> Highlights;
        // Only the highlighted blocks draw (the missing blocks of a reference grid).
        public bool HighlightedOnly;
        // Enclosed blocks too, without a cut (an X-ray sees through the hull).
        public bool IncludeEnclosed;
        // Every block not highlighted draws in this flat color instead of its paint.
        public bool HasTint;
        public Vector3 Tint;
        public GridHighlightStyle HighlightStyle;
        // Draws this snapshot instead of Grid: its blocks in the snapshot's cells, placed by SnapshotToWorld (the
        // snapshot's metres to world). Highlights then key the snapshot's positions.
        public GridSnapshot Snapshot;
        public MatrixD SnapshotToWorld;
    }

    // A line of the scene in its own coordinates (BlockLayer.SetLines).
    internal struct SceneLine
    {
        public Vector3D From;
        public Vector3D To;
        public Color Color;
    }

    /// <summary>
    /// Grid blocks as flat-lit geometry, a layer of the 3D scene sharing its depth buffer. Blocks with a
    /// model draw it, with its subparts (doors, rotor heads, piston tops), from the lowest-detail LOD the model
    /// lists, read by <see cref="MinimalMwmReader"/> and tinted with the average color of each material's
    /// texture; glass is left out, so windows are open. Armor has no model of its own: the game assembles it
    /// from side plate models, and so does this scene, through the game's own MyCubeGrid.GetCubeParts, leaving
    /// out the plates a neighbouring full armor cube covers. Small blocks, blocks whose models are not loaded
    /// yet and blocks past the triangle budget draw as their bounding box.
    /// <para>
    /// The game thread keeps, per grid, everything that only changes when the grid's blocks do (see
    /// <see cref="GridCache"/>); per frame <see cref="Capture"/> only takes the grids' matrices, the moving
    /// subparts and a few model loads. <see cref="Build"/>, on the paint threads, picks the visible blocks,
    /// nearest first within the budgets, and transforms and lights them in chunks; <see cref="Draw"/> runs on
    /// the row-band workers.
    /// </para>
    /// </summary>
    internal sealed class BlockLayer : ISceneLayer
    {
        // Budgets, the minimum block size and model loads per frame: Render3DSettings (AcruxSettings.xml).
        // Subpart nesting followed below a block (a piston's top, a turret's barrel on its base).
        const int MAXIMUM_SUBPART_DEPTH = 3;

        struct Instance
        {
            // Model (or grid) space to scene space.
            public MatrixD ToScene;
            public BlockModel Model;
            public Vector3 Minimum;
            public Vector3 Maximum;
            public Vector3 Paint;
            // Paint is a highlight: the color as is, without the model's texture tints.
            public bool Flat;
            // Not lit either: the color exactly, an overlay.
            public bool Unlit;
        }

        static readonly int[] BoxTriangles =
        {
            0, 3, 2, 0, 2, 1, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5
        };

        // Below this size on screen a block draws as its box: its model's detail would not show, and a large
        // ship's distant blocks would spend the triangle budget the near ones need.
        const double MODEL_MINIMUM_PIXELS = 8d;

        // ---- Per grid, on the game thread --------------------------------------------------------------

        /// <summary>
        /// One block the camera could see (not enclosed by full armor cubes), in its grid's local metres. The
        /// game thread writes it; paint threads read it. <see cref="Model"/> and <see cref="Armor"/> start null
        /// while their models load and are then set once (a reference write, seen whole or not at all).
        /// </summary>
        sealed class CachedBlock
        {
            // Game thread only. Slim is null for a snapshot's block.
            public IMySlimBlock Slim;
            public GridCache Grid;
            public string Asset;
            public bool Pending;
            public MyCubeBlockDefinition Definition;
            public MyBlockOrientation Orientation;
            public bool HasEntity;
            // A snapshot block's moving parts, in its Subparts.
            public int FirstSubpart;
            public int SubpartCount;
            // What frames know the block by across cache refreshes: its slim block, or itself.
            public object Key;

            public Vector3 Center;
            public float Radius;
            public Vector3 BoxMinimum;
            public Vector3 BoxMaximum;
            public Vector3I Min;
            public Vector3I Max;
            // IMySlimBlock.Position: the key of a grid view's highlights.
            public Vector3I Position;
            public Vector3 Paint;
            // Model space to grid space, for a block with a model of its own.
            public MatrixD Local;
            public volatile BlockModel Model;
            public volatile ArmorSet Armor;
            public volatile bool Glass;
            // Seen from outside the grid (set by the hull flood before it publishes).
            public bool Outside;
        }

        // An armor block's visible plates, in grid space.
        sealed class ArmorSet
        {
            public BlockModel[] Models;
            public MatrixD[] Locals;
            public int Triangles;
        }

        /// <summary>
        /// Per grid, rebuilt only when its blocks change: the cells holding a full armor cube (they hide what
        /// lies behind their sides), and the blocks not enclosed by such cubes on every side, cached. A large
        /// ship's interior (most of its blocks) is then never visited, and its armor's inner plates are left
        /// out. A refresh publishes a new <see cref="Blocks"/> array; frames in flight keep the one they took.
        /// </summary>
        sealed class GridCache
        {
            // Set for a snapshot's cache, which is built once.
            public GridSnapshot Snapshot;
            public readonly HashSet<Vector3I> Full = new HashSet<Vector3I>();
            // What frames draw from outside the grid's box: the blocks the hull flood reached, or, until the
            // first flood is done, every block not enclosed.
            public volatile CachedBlock[] Blocks = new CachedBlock[0];
            // Every block, the enclosed ones included, for a cut-open view; built only once one asks (WantsAll).
            public volatile CachedBlock[] All;
            public bool WantsAll;
            // Seen from inside: the blocks the flood from the camera's cell reached that the hull one did not.
            public volatile CachedBlock[] CameraBlocks;
            public volatile HullFlood Flood;
            public Vector3I CameraCell;
            public HullFlood CameraFlood;
            public bool CameraRunning;
            public int Generation;
            // Blocks with subparts (doors, turrets, pistons): their parts move, so they are read every frame.
            public readonly List<CachedBlock> Animated = new List<CachedBlock>();
            public float GridSize;
            public int BlockCount = -1;
            public DateTime Refreshed;
            public DateTime LastUsed;
        }

        // A grid whose block count changed refreshes at most this often (welding changes it every few frames).
        const double REFRESH_SECONDS = 1d;
        // And at least this often, for repainted blocks and blocks replaced by others (the count misses them).
        const double MAXIMUM_CACHE_AGE_SECONDS = 10d;
        // A grid not captured for this long drops its cache.
        const double FORGET_SECONDS = 30d;

        static readonly Dictionary<long, GridCache> GridCaches = new Dictionary<long, GridCache>();
        static readonly List<IMySlimBlock> CacheBlocks = new List<IMySlimBlock>();
        static readonly List<SourceBlock> CacheSources = new List<SourceBlock>();
        static readonly List<CachedBlock> CacheExposed = new List<CachedBlock>();
        static readonly List<CachedBlock> CacheEnclosed = new List<CachedBlock>();
        static readonly List<long> ForgottenGrids = new List<long>();
        static DateTime _nextForget;

        // Visible blocks still waiting for their models, nearest first, from the last Build to the next Capture.
        const int MAXIMUM_REQUESTS = 128;
        static readonly object RequestGate = new object();
        static readonly List<CachedBlock> Requests = new List<CachedBlock>();
        static readonly List<CachedBlock> RequestsTaken = new List<CachedBlock>();

        // ---- Per frame (this layer is one frame slot's) ------------------------------------------------

        struct FrameGrid
        {
            public CachedBlock[] Blocks;
            // Seen from the camera inside the grid's hull, beyond Blocks; null outside.
            public CachedBlock[] CameraBlocks;
            public MatrixD GridToScene;
            public bool HasExclude;
            // The excluded point (the screen's own block), in the grid's cells.
            public Vector3D ExcludeCell;
            public Dictionary<Vector3I, Vector3> Highlights;
            public bool HighlightedOnly;
            public bool HasTint;
            public Vector3 Tint;
            public GridHighlightStyle HighlightStyle;
        }

        struct FrameSubpart
        {
            public CachedBlock Owner;
            public int Grid;
            public BlockModel Model;
            public MatrixD ToScene;
        }

        struct Candidate
        {
            public CachedBlock Block;
            public int Grid;
            public float Distance;
            public float Pixels;
        }

        // Visibility runs on the paint threads in work items of at most this many blocks.
        const int VISIBILITY_ITEM_BLOCKS = 2048;
        // Nearest first by distance buckets: this many per doubling of the distance, from 1 m.
        const int BUCKETS_PER_OCTAVE = 8;
        const int BUCKET_COUNT = BUCKETS_PER_OCTAVE * 24;

        readonly List<FrameGrid> _frameGrids = new List<FrameGrid>();
        SceneCut _cut;
        Vector3D _cameraWorld;
        DateTime _now;
        readonly List<FrameSubpart> _subparts = new List<FrameSubpart>();
        readonly List<IMyEntity> _entities = new List<IMyEntity>();
        readonly List<Instance> _instances = new List<Instance>();
        // By CachedBlock.Key: a cache refresh makes new CachedBlocks for the animated list at once, while the drawn list
        // keeps the old ones until its hull flood publishes, so the two only meet through the game's block.
        readonly HashSet<object> _detailed = new HashSet<object>();
        readonly List<CachedBlock> _requests = new List<CachedBlock>();
        struct VisibilityItem
        {
            public int Grid;
            public CachedBlock[] Blocks;
            public int First;
            public int End;
        }

        readonly List<VisibilityItem> _items = new List<VisibilityItem>();
        readonly List<List<Candidate>> _itemCandidates = new List<List<Candidate>>();
        readonly int[] _bucketCounts = new int[BUCKET_COUNT + 1];
        Candidate[] _sorted = new Candidate[0];
        Action<int> _visibilityItem;
        double _minimumPixels;

        // Build splits the instances into chunks of about equal triangle count, built on every paint thread
        // into their own lists; Draw rasterizes them all (depth-tested, so their order does not matter).
        sealed class BuildChunk
        {
            public readonly SceneTriangleList Triangles = new SceneTriangleList();
            public readonly Vector3D[] Corners = new Vector3D[8];
            public Vector3D[] Transformed = new Vector3D[0];
            public int First;
            public int End;
            public Exception Failure;
        }

        // Chunks per paint thread: more than one balances instances whose real cost the count misses.
        const int CHUNKS_PER_THREAD = 4;
        // A chunk is at least this many triangles: a small scene builds on one thread without the overhead.
        const int MINIMUM_CHUNK_TRIANGLES = 4096;
        // What a block without a loaded model costs: its bounding box.
        const int BOX_TRIANGLES = 12;

        readonly List<BuildChunk> _chunks = new List<BuildChunk>();
        int _chunkCount;
        Action<int> _buildChunk;
        SceneCamera _camera;
        Vector3 _light;

        internal static void ReleaseShared()
        {
            GridCaches.Clear();
            lock (RequestGate)
                Requests.Clear();
            ArmorParts.Clear();
            FailedArmor.Clear();
        }


        public string PendingWork { get; private set; }

        // Game thread. The block holding ExcludeWorld (the screen the camera looks out of) is left out; the rest of
        // its grid draws, so its walls hide what is behind them.
        public void Capture(ref SceneCapture capture)
        {
            int loads;
            if (!BeginCapture(ref capture, default(SceneCut), out loads))
                return;
            var sphere = new BoundingSphereD(capture.CenterWorld, capture.Radius);
            _entities.AddRange(MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere));
            for (int e = 0; e < _entities.Count; e++)
            {
                var grid = _entities[e] as IMyCubeGrid;
                if (grid != null && !grid.MarkedForClose &&
                    (capture.Only == null || capture.Only.Contains(grid.EntityId)))
                    AddGrid(new BlockGridView { Grid = grid }, ref capture, ref loads);
            }
            _entities.Clear();
            EndCapture(ref loads);
        }

        // Game thread. Only the given grids, each with its own highlights, and only what lies inside the cut box.
        public void Capture(ref SceneCapture capture, List<BlockGridView> grids, SceneCut cut)
        {
            int loads;
            if (!BeginCapture(ref capture, cut, out loads))
                return;
            for (int i = 0; i < grids.Count; i++)
            {
                IMyCubeGrid grid = grids[i].Grid;
                if (grids[i].Snapshot != null || (grid != null && !grid.MarkedForClose))
                    AddGrid(grids[i], ref capture, ref loads);
            }
            EndCapture(ref loads);
        }

        bool BeginCapture(ref SceneCapture capture, SceneCut cut, out int loads)
        {
            _camera = capture.Camera;
            _light = capture.Light;
            _cut = cut;
            _cameraWorld = Vector3D.Transform(_camera.Position, MatrixD.Invert(capture.WorldToScene));
            _frameGrids.Clear();
            _subparts.Clear();
            _entities.Clear();
            PendingWork = null;
            Render3DSettings settings = Render3DSettings.Current;
            _minimumPixels = settings.MinimumBlockPixels;
            loads = settings.BlockModelLoadsPerFrame;
            if (MyAPIGateway.Entities == null)
                return false;
            _now = DateTime.UtcNow;
            ForgetUnusedGrids(_now);
            return true;
        }

        void AddGrid(BlockGridView view, ref SceneCapture capture, ref int loads)
        {
            IMyCubeGrid grid = view.Grid;
            GridSnapshot snapshot = view.Snapshot;
            MatrixD worldToScene = capture.WorldToScene;
            bool all = _cut.Enabled || view.IncludeEnclosed;
            GridCache cache = snapshot != null ? GetSnapshotCache(snapshot, _now, all) : GetGridCache(grid, _now, all);
            MatrixD gridWorld = snapshot != null ? view.SnapshotToWorld : grid.WorldMatrix;
            var frame = new FrameGrid
            {
                Blocks = all ? cache.All ?? cache.Blocks : cache.Blocks,
                GridToScene = gridWorld * worldToScene,
                Highlights = view.Highlights,
                HighlightedOnly = view.HighlightedOnly,
                HasTint = view.HasTint,
                Tint = view.Tint,
                HighlightStyle = view.HighlightStyle
            };
            int index = _frameGrids.Count;
            if (snapshot != null)
            {
                _frameGrids.Add(frame);
                AddSnapshotSubparts(cache, frame.GridToScene, index, ref loads);
                return;
            }
            MatrixD worldToGrid = MatrixD.Invert(gridWorld);
            // With every block, what the camera sees from inside is in Blocks already.
            if (!all && grid.WorldAABB.Contains(_cameraWorld) != ContainmentType.Disjoint)
                frame.CameraBlocks = CameraBlocksFor(cache,
                    Vector3D.Transform(_cameraWorld, worldToGrid) / grid.GridSize);
            // The excluded point in this grid's cells, when it lies inside the grid's box at all.
            Vector3D? excludeWorld = capture.ExcludeWorld;
            if (excludeWorld.HasValue && grid.WorldAABB.Contains(excludeWorld.Value) != ContainmentType.Disjoint)
            {
                frame.HasExclude = true;
                frame.ExcludeCell = Vector3D.Transform(excludeWorld.Value, worldToGrid) / grid.GridSize;
            }
            _frameGrids.Add(frame);

            for (int i = 0; i < cache.Animated.Count; i++)
            {
                CachedBlock owner = cache.Animated[i];
                IMyCubeBlock fat = owner.Slim.FatBlock;
                if (fat != null && !fat.Closed)
                    AddSubparts(fat as MyEntity, worldToScene, owner, index, 0, ref loads);
            }
        }

        // A snapshot's moving parts stand as they were captured.
        void AddSnapshotSubparts(GridCache cache, MatrixD gridToScene, int grid, ref int loads)
        {
            GridSnapshot snapshot = cache.Snapshot;
            for (int i = 0; i < cache.Animated.Count; i++)
            {
                CachedBlock owner = cache.Animated[i];
                for (int s = 0; s < owner.SubpartCount; s++)
                {
                    GridSnapshot.Subpart subpart = snapshot.Subparts[owner.FirstSubpart + s];
                    BlockModel mesh = ResolveModel(snapshot.Models[subpart.Model], ref loads);
                    if (mesh == null)
                        continue;
                    _subparts.Add(new FrameSubpart
                    {
                        Owner = owner,
                        Grid = grid,
                        Model = mesh,
                        ToScene = (MatrixD)subpart.Matrix * gridToScene
                    });
                }
            }
        }

        void EndCapture(ref int loads)
        {
            // The models the last frame's visible blocks were missing, nearest first.
            lock (RequestGate)
            {
                RequestsTaken.AddRange(Requests);
                Requests.Clear();
            }
            for (int i = 0; i < RequestsTaken.Count && loads > 0; i++)
                TryResolve(RequestsTaken[i], ref loads);
            if (RequestsTaken.Count > 0)
                PendingWork = "block models";
            RequestsTaken.Clear();
        }

        // A block's moving parts are child entities with their own models and matrices.
        void AddSubparts(MyEntity entity, MatrixD worldToScene, CachedBlock owner, int grid, int depth, ref int loads)
        {
            if (entity == null || entity.Subparts == null || depth >= MAXIMUM_SUBPART_DEPTH)
                return;
            foreach (MyEntitySubpart subpart in entity.Subparts.Values)
            {
                if (subpart == null || subpart.MarkedForClose)
                    continue;
                IMyModel model = ((IMyEntity)subpart).Model;
                BlockModel mesh = model == null ? null : ResolveModel(model.AssetName, ref loads);
                if (mesh != null)
                {
                    _subparts.Add(new FrameSubpart
                    {
                        Owner = owner,
                        Grid = grid,
                        Model = mesh,
                        ToScene = subpart.WorldMatrix * worldToScene
                    });
                }
                AddSubparts(subpart, worldToScene, owner, grid, depth + 1, ref loads);
            }
        }

        static GridCache GetGridCache(IMyCubeGrid grid, DateTime now, bool wantsAll)
        {
            GridCache cache;
            if (!GridCaches.TryGetValue(grid.EntityId, out cache))
            {
                cache = new GridCache();
                GridCaches[grid.EntityId] = cache;
            }
            cache.LastUsed = now;
            var cubeGrid = grid as MyCubeGrid;
            int count = cubeGrid != null ? cubeGrid.BlocksCount : -2;
            double age = (now - cache.Refreshed).TotalSeconds;
            bool missingAll = wantsAll && cache.All == null;
            cache.WantsAll |= wantsAll;
            if (cache.BlockCount < 0 || age >= MAXIMUM_CACHE_AGE_SECONDS || missingAll ||
                (count != cache.BlockCount && age >= REFRESH_SECONDS))
                RefreshGridCache(grid, cache, now);
            return cache;
        }

        static GridCache GetSnapshotCache(GridSnapshot snapshot, DateTime now, bool wantsAll)
        {
            GridCache cache;
            if (!GridCaches.TryGetValue(snapshot.CacheKey, out cache))
            {
                cache = new GridCache { Snapshot = snapshot };
                GridCaches[snapshot.CacheKey] = cache;
            }
            cache.LastUsed = now;
            bool missingAll = wantsAll && cache.All == null;
            cache.WantsAll |= wantsAll;
            if (cache.BlockCount < 0 || missingAll)
                RefreshSnapshotCache(snapshot, cache, now);
            return cache;
        }

        // What a cached block is made from: a live block's values, or a snapshot's.
        struct SourceBlock
        {
            public IMySlimBlock Slim;
            public MyCubeBlockDefinition Definition;
            public MyBlockOrientation Orientation;
            public Vector3I Position;
            public Vector3I Min;
            public Vector3I Max;
            public Vector3 ColorMaskHsv;
            public string Asset;
            public MatrixD Local;
            public bool HasEntity;
            public bool Animated;
            public bool FullCube;
            public bool Wall;
            public int FirstSubpart;
            public int SubpartCount;
        }

        static void RefreshGridCache(IMyCubeGrid grid, GridCache cache, DateTime now)
        {
            cache.Refreshed = now;
            cache.GridSize = grid.GridSize;
            CacheBlocks.Clear();
            grid.GetBlocks(CacheBlocks);
            MatrixD worldToGrid = MatrixD.Invert(grid.WorldMatrix);
            for (int i = 0; i < CacheBlocks.Count; i++)
            {
                IMySlimBlock slim = CacheBlocks[i];
                IMyCubeBlock fat = slim.FatBlock;
                var source = new SourceBlock
                {
                    Slim = slim,
                    Definition = slim.BlockDefinition as MyCubeBlockDefinition,
                    Orientation = slim.Orientation,
                    Position = slim.Position,
                    Min = slim.Min,
                    Max = slim.Max,
                    ColorMaskHsv = slim.ColorMaskHSV,
                    HasEntity = fat != null,
                    FullCube = IsFullCube(slim),
                    Wall = IsWall(slim)
                };
                IMyModel model = fat == null ? null : fat.Model;
                if (model != null && !string.IsNullOrEmpty(model.AssetName))
                {
                    source.Asset = model.AssetName;
                    source.Local = fat.WorldMatrix * worldToGrid;
                    var entity = fat as MyEntity;
                    source.Animated = entity != null && entity.Subparts != null && entity.Subparts.Count > 0;
                }
                CacheSources.Add(source);
            }
            CacheBlocks.Clear();
            BuildCache(cache);
        }

        static void RefreshSnapshotCache(GridSnapshot snapshot, GridCache cache, DateTime now)
        {
            cache.Refreshed = now;
            cache.GridSize = snapshot.GridSize;
            GridSnapshot.Block[] blocks = snapshot.Blocks;
            for (int i = 0; i < blocks.Length; i++)
            {
                GridSnapshot.Block block = blocks[i];
                GridSnapshot.Definition definition = snapshot.Definitions[block.Definition];
                MyCubeBlockDefinition cubeDefinition;
                MyDefinitionManager.Static.TryGetCubeBlockDefinition(definition.Id, out cubeDefinition);
                var source = new SourceBlock
                {
                    Definition = cubeDefinition,
                    Orientation = new MyBlockOrientation(block.Forward, block.Up),
                    Position = block.Position,
                    Min = block.Min,
                    Max = block.Max,
                    ColorMaskHsv = snapshot.Colors[block.Color],
                    HasEntity = (definition.Flags & GridSnapshot.FLAG_ENTITY) != 0,
                    FullCube = (definition.Flags & GridSnapshot.FLAG_FULL_CUBE) != 0,
                    Wall = (definition.Flags & GridSnapshot.FLAG_WALL) != 0
                };
                if (block.Local >= 0)
                {
                    GridSnapshot.Local local = snapshot.Locals[block.Local];
                    source.Asset = snapshot.Models[local.Model];
                    source.Local = local.Matrix;
                    source.FirstSubpart = local.FirstSubpart;
                    source.SubpartCount = local.SubpartCount;
                    source.Animated = local.SubpartCount > 0;
                }
                CacheSources.Add(source);
            }
            BuildCache(cache);
        }

        static void BuildCache(GridCache cache)
        {
            cache.BlockCount = CacheSources.Count;
            HashSet<Vector3I> full = cache.Full;
            full.Clear();
            for (int i = 0; i < CacheSources.Count; i++)
            {
                if (CacheSources[i].FullCube)
                    full.Add(CacheSources[i].Position);
            }

            cache.Animated.Clear();
            CacheExposed.Clear();
            int loads = 0;
            for (int i = 0; i < CacheSources.Count; i++)
            {
                SourceBlock source = CacheSources[i];
                if (IsEnclosed(source.Min, source.Max, full))
                {
                    if (cache.WantsAll)
                        CacheEnclosed.Add(CreateBlock(ref source, cache, ref loads));
                    continue;
                }
                CacheExposed.Add(CreateBlock(ref source, cache, ref loads));
            }
            if (cache.WantsAll)
            {
                CacheEnclosed.AddRange(CacheExposed);
                cache.All = CacheEnclosed.ToArray();
            }
            StartHullFlood(cache, CacheSources, CacheExposed.ToArray());
            CacheExposed.Clear();
            CacheEnclosed.Clear();
            CacheSources.Clear();
        }

        static CachedBlock CreateBlock(ref SourceBlock source, GridCache cache, ref int loads)
        {
            float size = cache.GridSize;
            float half = size * 0.5f;
            Vector3I min = source.Min;
            Vector3I max = source.Max;
            var block = new CachedBlock
            {
                Slim = source.Slim,
                Grid = cache,
                Definition = source.Definition,
                Orientation = source.Orientation,
                HasEntity = source.HasEntity,
                Min = min,
                Max = max,
                Position = source.Position,
                Center = new Vector3(min + max) * half,
                Radius = (new Vector3(max - min) + Vector3.One).Length() * half,
                BoxMinimum = new Vector3(min) * size - new Vector3(half),
                BoxMaximum = new Vector3(max) * size + new Vector3(half),
                Paint = MyColorPickerConstants.HSVOffsetToHSV(source.ColorMaskHsv).HSVtoColor().ToVector3(),
                Asset = source.Asset,
                Local = source.Local,
                FirstSubpart = source.FirstSubpart,
                SubpartCount = source.SubpartCount
            };
            block.Key = (object)source.Slim ?? block;
            if (source.Animated)
                cache.Animated.Add(block);
            // Only what is loaded already; the rest loads nearest first as the frames ask for it.
            block.Pending = true;
            TryResolve(block, ref loads);
            return block;
        }

        // Fills in a block's model or armor plates once loaded; loads count against the frame's budget.
        static void TryResolve(CachedBlock block, ref int loads)
        {
            if (!block.Pending)
                return;
            IMySlimBlock slim = block.Slim;
            if (slim != null && slim.IsDestroyed)
            {
                block.Pending = false;
                return;
            }
            if (block.Asset != null)
            {
                BlockModel model = ResolveModel(block.Asset, ref loads);
                if (model != null)
                {
                    block.Glass = model.HasGlass;
                    block.Model = model;
                    block.Pending = false;
                }
                else if (MeshAssets.HasFailed(block.Asset))
                {
                    block.Pending = false;
                }
                return;
            }
            if (block.HasEntity || block.Definition == null)
            {
                // A block with an entity but no model of its own: its box.
                block.Pending = false;
                return;
            }
            bool failed;
            ArmorSet armor = BuildArmor(block, ref loads, out failed);
            if (armor != null)
            {
                block.Armor = armor;
                block.Pending = false;
            }
            else if (failed)
            {
                block.Pending = false;
            }
        }

        // A one-cell armor cube: it fills its cell, so nothing behind any of its sides shows through.
        static bool IsFullCube(IMySlimBlock slim)
        {
            if (slim.FatBlock != null || slim.Min != slim.Max)
                return false;
            var definition = slim.BlockDefinition as MyCubeBlockDefinition;
            return definition != null && definition.CubeDefinition != null &&
                   definition.CubeDefinition.CubeTopology == MyCubeTopology.Box;
        }

        // Every cell touching the block's box from outside holds a full cube.
        static bool IsEnclosed(Vector3I min, Vector3I max, HashSet<Vector3I> full)
        {
            if (full.Count == 0)
                return false;
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    if (!full.Contains(new Vector3I(min.X - 1, y, z)) || !full.Contains(new Vector3I(max.X + 1, y, z)))
                        return false;
            for (int x = min.X; x <= max.X; x++)
                for (int z = min.Z; z <= max.Z; z++)
                    if (!full.Contains(new Vector3I(x, min.Y - 1, z)) || !full.Contains(new Vector3I(x, max.Y + 1, z)))
                        return false;
            for (int x = min.X; x <= max.X; x++)
                for (int y = min.Y; y <= max.Y; y++)
                    if (!full.Contains(new Vector3I(x, y, min.Z - 1)) || !full.Contains(new Vector3I(x, y, max.Z + 1)))
                        return false;
            return true;
        }

        static void ForgetUnusedGrids(DateTime now)
        {
            if (now < _nextForget)
                return;
            _nextForget = now.AddSeconds(FORGET_SECONDS);
            foreach (KeyValuePair<long, GridCache> entry in GridCaches)
            {
                if ((now - entry.Value.LastUsed).TotalSeconds >= FORGET_SECONDS)
                    ForgottenGrids.Add(entry.Key);
            }
            for (int i = 0; i < ForgottenGrids.Count; i++)
                GridCaches.Remove(ForgottenGrids[i]);
            ForgottenGrids.Clear();
        }

        /// <summary>
        /// The grid's cells, one byte each over its box grown by one cell: walls (fully airtight blocks and
        /// full armor cubes) and the air the hull flood reached from outside. Immutable once published.
        /// </summary>
        sealed class HullFlood
        {
            public const byte WALL = 1;
            public const byte OUTSIDE = 2;
            public Vector3I Origin;
            public Vector3I Size;
            public byte[] Cells;
            public CachedBlock[] Candidates;
            public int Generation;

            public int IndexOf(Vector3I cell)
            {
                Vector3I local = cell - Origin;
                if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= Size.X || local.Y >= Size.Y ||
                    local.Z >= Size.Z)
                    return -1;
                return (local.Z * Size.Y + local.Y) * Size.X + local.X;
            }
        }

        // Larger boxes (in cells) are not flooded: every block not enclosed is drawn instead.
        const long MAXIMUM_FLOOD_CELLS = 48L * 1024 * 1024;

        // A fully airtight block (the game's own flag, windows included: what is inside a ship does not need
        // to show through them) or a full armor cube walls off what is behind it.
        static bool IsWall(IMySlimBlock slim)
        {
            var definition = slim.BlockDefinition as MyCubeBlockDefinition;
            if (definition == null)
                return false;
            if (definition.IsAirTight == true)
                return true;
            return slim.FatBlock == null && definition.CubeDefinition != null &&
                   definition.CubeDefinition.CubeTopology == MyCubeTopology.Box;
        }

        // Game thread, after a refresh: snapshots the walls and floods on a background task. Frames keep the
        // last published blocks until it is done.
        static void StartHullFlood(GridCache cache, List<SourceBlock> blocks, CachedBlock[] candidates)
        {
            if (blocks.Count == 0 || MyAPIGateway.Parallel == null)
            {
                cache.Blocks = candidates;
                return;
            }
            Vector3I min = new Vector3I(int.MaxValue);
            Vector3I max = new Vector3I(int.MinValue);
            var walls = new List<Vector3I>();
            for (int i = 0; i < blocks.Count; i++)
            {
                SourceBlock block = blocks[i];
                min = Vector3I.Min(min, block.Min);
                max = Vector3I.Max(max, block.Max);
                if (block.Wall)
                {
                    walls.Add(block.Min);
                    walls.Add(block.Max);
                }
            }
            Vector3I size = max - min + new Vector3I(3);
            if ((long)size.X * size.Y * size.Z > MAXIMUM_FLOOD_CELLS)
            {
                cache.Blocks = candidates;
                return;
            }
            var flood = new HullFlood
            {
                Origin = min - Vector3I.One,
                Size = size,
                Candidates = candidates,
                Generation = ++cache.Generation
            };
            if (cache.Flood == null)
                cache.Blocks = candidates;
            try
            {
                MyAPIGateway.Parallel.StartBackground(() => RunHullFlood(cache, flood, walls));
            }
            catch (Exception)
            {
                cache.Blocks = candidates;
            }
        }

        static void RunHullFlood(GridCache cache, HullFlood flood, List<Vector3I> walls)
        {
            try
            {
                Vector3I size = flood.Size;
                var cells = new byte[size.X * size.Y * size.Z];
                for (int i = 0; i + 1 < walls.Count; i += 2)
                {
                    Vector3I a = walls[i];
                    Vector3I b = walls[i + 1];
                    for (int z = a.Z; z <= b.Z; z++)
                        for (int y = a.Y; y <= b.Y; y++)
                            for (int x = a.X; x <= b.X; x++)
                                cells[flood.IndexOf(new Vector3I(x, y, z))] = HullFlood.WALL;
                }
                flood.Cells = cells;

                // Every cell of the grown box's boundary is outside the grid.
                var queue = new List<int>();
                for (int z = 0; z < size.Z; z++)
                    for (int y = 0; y < size.Y; y++)
                        for (int x = 0; x < size.X; x++)
                        {
                            if (x != 0 && y != 0 && z != 0 && x != size.X - 1 && y != size.Y - 1 && z != size.Z - 1)
                                continue;
                            int index = (z * size.Y + y) * size.X + x;
                            if (cells[index] == 0)
                            {
                                cells[index] = HullFlood.OUTSIDE;
                                queue.Add(index);
                            }
                        }
                Fill(cells, size, queue, HullFlood.OUTSIDE);

                CachedBlock[] candidates = flood.Candidates;
                var reached = new List<CachedBlock>(candidates.Length / 4);
                for (int i = 0; i < candidates.Length; i++)
                {
                    CachedBlock block = candidates[i];
                    block.Outside = Touches(flood, cells, block.Min, block.Max, HullFlood.OUTSIDE);
                    if (block.Outside)
                        reached.Add(block);
                }
                if (cache.Generation != flood.Generation)
                    return;
                cache.Blocks = reached.ToArray();
                cache.Flood = flood;
            }
            catch (Exception)
            {
                // A failed flood leaves the last published blocks.
            }
        }

        // Six-connected flood through cells holding neither a wall nor the mark.
        static void Fill(byte[] cells, Vector3I size, List<int> queue, byte mark)
        {
            int strideY = size.X;
            int strideZ = size.X * size.Y;
            for (int head = 0; head < queue.Count; head++)
            {
                int index = queue[head];
                int x = index % size.X;
                int y = index / strideY % size.Y;
                int z = index / strideZ;
                if (x > 0) Visit(cells, index - 1, mark, queue);
                if (x < size.X - 1) Visit(cells, index + 1, mark, queue);
                if (y > 0) Visit(cells, index - strideY, mark, queue);
                if (y < size.Y - 1) Visit(cells, index + strideY, mark, queue);
                if (z > 0) Visit(cells, index - strideZ, mark, queue);
                if (z < size.Z - 1) Visit(cells, index + strideZ, mark, queue);
            }
        }

        static void Visit(byte[] cells, int index, byte mark, List<int> queue)
        {
            if ((cells[index] & (HullFlood.WALL | mark)) != 0)
                return;
            cells[index] |= mark;
            queue.Add(index);
        }

        // Whether a cell touching the block's box from outside (or the box itself, for a block that is no
        // wall) carries the mark.
        static bool Touches(HullFlood flood, byte[] cells, Vector3I min, Vector3I max, byte mark)
        {
            for (int z = min.Z - 1; z <= max.Z + 1; z++)
                for (int y = min.Y - 1; y <= max.Y + 1; y++)
                    for (int x = min.X - 1; x <= max.X + 1; x++)
                    {
                        // Only the six face neighbours' layers and the box itself, not its edges and corners.
                        int outside = (x < min.X || x > max.X ? 1 : 0) + (y < min.Y || y > max.Y ? 1 : 0) +
                                      (z < min.Z || z > max.Z ? 1 : 0);
                        if (outside > 1)
                            continue;
                        int index = flood.IndexOf(new Vector3I(x, y, z));
                        if (index < 0 || (cells[index] & mark) != 0)
                            return true;
                    }
            return false;
        }

        // The camera inside the grid's box: the blocks around the air it can reach, beyond what the outside
        // sees, on a background task cached per camera cell.
        const byte CAMERA = 4;

        static CachedBlock[] CameraBlocksFor(GridCache cache, Vector3D cameraCell)
        {
            HullFlood flood = cache.Flood;
            if (flood == null)
                return null;
            var cell = new Vector3I((int)Math.Round(cameraCell.X), (int)Math.Round(cameraCell.Y),
                (int)Math.Round(cameraCell.Z));
            int index = flood.IndexOf(cell);
            // Outside the box, or in air the hull flood reached: the outside's blocks are what it sees.
            if (index < 0 || (flood.Cells[index] & HullFlood.OUTSIDE) != 0)
                return null;
            if (cache.CameraFlood == flood && cache.CameraCell == cell)
                return cache.CameraBlocks;
            if (!cache.CameraRunning)
            {
                cache.CameraRunning = true;
                cache.CameraFlood = flood;
                cache.CameraCell = cell;
                try
                {
                    MyAPIGateway.Parallel.StartBackground(() => RunCameraFlood(cache, flood, index));
                }
                catch (Exception)
                {
                    cache.CameraRunning = false;
                }
            }
            // Until it is done, the last camera blocks (the view moved within the grid).
            return cache.CameraBlocks;
        }

        static void RunCameraFlood(GridCache cache, HullFlood flood, int start)
        {
            try
            {
                var cells = (byte[])flood.Cells.Clone();
                // The camera's own cell is air for it, even inside a wall block (a screen set into the hull).
                cells[start] = (byte)((cells[start] & ~HullFlood.WALL) | CAMERA);
                var queue = new List<int> { start };
                Fill(cells, flood.Size, queue, CAMERA);
                var reached = new List<CachedBlock>();
                CachedBlock[] candidates = flood.Candidates;
                for (int i = 0; i < candidates.Length; i++)
                {
                    CachedBlock block = candidates[i];
                    if (!block.Outside && Touches(flood, cells, block.Min, block.Max, CAMERA))
                        reached.Add(block);
                }
                cache.CameraBlocks = reached.ToArray();
            }
            catch (Exception)
            {
                cache.CameraBlocks = null;
            }
            finally
            {
                cache.CameraRunning = false;
            }
        }

        // An armor block as the game assembles it: one plate model per side (the definition's CubeDefinition
        // side models), placed by its topology's tiles (MyCubeGrid.GetCubeParts, as the block builder's preview
        // does), less the plates on sides a full armor cube covers. The plates load like any model, at their
        // lowest LOD. Null until every plate has loaded (the block keeps its box rather than showing half its
        // sides); failed when the block is not armor or cannot be assembled.
        static ArmorSet BuildArmor(CachedBlock block, ref int loads, out bool failed)
        {
            failed = true;
            MyCubeBlockDefinition definition = block.Definition;
            GridCache grid = block.Grid;
            if (definition == null || definition.CubeDefinition == null)
                return null;
            ArmorPart[] parts = GetArmorParts(definition, block.Orientation, grid.GridSize);
            if (parts == null || parts.Length == 0)
                return null;
            failed = false;

            HashSet<Vector3I> full = grid.Full;
            Vector3I position = block.Position;
            int shown = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].HasSide && full.Contains(position + parts[i].Side))
                    continue;
                if (ResolveModel(parts[i].Model, ref loads) == null)
                {
                    failed = MeshAssets.HasFailed(parts[i].Model);
                    return null;
                }
                shown++;
            }

            // The game places the parts around the block's cell; MyCubeGrid uses its Position the same way.
            MatrixD cell = MatrixD.CreateTranslation(new Vector3D(position) * grid.GridSize);
            var armor = new ArmorSet { Models = new BlockModel[shown], Locals = new MatrixD[shown] };
            int index = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].HasSide && full.Contains(position + parts[i].Side))
                    continue;
                BlockModel mesh = MeshAssets.Loaded(parts[i].Model);
                armor.Models[index] = mesh;
                armor.Locals[index] = parts[i].Local * cell;
                armor.Triangles += mesh.TriangleCount;
                index++;
            }
            return armor;
        }

        struct ArmorPart
        {
            public string Model;
            public MatrixD Local;
            // The cell side the plate lies on (grid axes), when it faces straight along one: slopes have none.
            public Vector3I Side;
            public bool HasSide;
        }

        // Per definition, per orientation (Forward * 6 + Up): the plates, relative to the block's cell.
        static readonly Dictionary<MyDefinitionId, ArmorPart[][]> ArmorParts =
            new Dictionary<MyDefinitionId, ArmorPart[][]>();
        static readonly List<string> PartModels = new List<string>();
        // Failed loads and when to try them again.
        const double RETRY_SECONDS = 20d;
        static readonly Dictionary<string, DateTime> FailedArmor = new Dictionary<string, DateTime>();

        static string ArmorKey(MyCubeBlockDefinition definition, int orientation)
        {
            return definition.Id.ToString() + "|" + orientation;
        }
        static readonly List<MatrixD> PartMatrices = new List<MatrixD>();
        static readonly List<Vector3> PartNormals = new List<Vector3>();
        static readonly List<Vector4UByte> PartPatterns = new List<Vector4UByte>();

        static ArmorPart[] GetArmorParts(MyCubeBlockDefinition definition, MyBlockOrientation orientation, float gridSize)
        {
            ArmorPart[][] byOrientation;
            if (!ArmorParts.TryGetValue(definition.Id, out byOrientation))
            {
                byOrientation = new ArmorPart[36][];
                ArmorParts[definition.Id] = byOrientation;
            }
            int index = (int)orientation.Forward * 6 + (int)orientation.Up;
            ArmorPart[] parts = byOrientation[index];
            if (parts != null)
                return parts;
            DateTime retry;
            if (FailedArmor.TryGetValue(ArmorKey(definition, index), out retry) && DateTime.UtcNow < retry)
                return null;

            Matrix rotation = Matrix.CreateWorld(Vector3.Zero,
                Base6Directions.GetVector(orientation.Forward), Base6Directions.GetVector(orientation.Up));
            try
            {
                MyCubeGrid.GetCubeParts(definition, Vector3I.Zero, rotation, gridSize, PartModels, PartMatrices,
                    PartNormals, PartPatterns, true);
                parts = new ArmorPart[PartModels.Count];
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = new ArmorPart { Model = PartModels[i], Local = PartMatrices[i] };
                    // GetCubeParts returns each plate's normal already rotated into grid axes.
                    Vector3 normal = i < PartNormals.Count ? PartNormals[i] : Vector3.Zero;
                    Vector3 absolute = Vector3.Abs(normal);
                    if (Math.Max(absolute.X, Math.Max(absolute.Y, absolute.Z)) > 0.99f &&
                        absolute.X + absolute.Y + absolute.Z < 1.01f)
                    {
                        parts[i].Side = Vector3I.Round(normal);
                        parts[i].HasSide = true;
                    }
                }
            }
            catch (Exception)
            {
                // A definition the game cannot assemble yet keeps its box, and is asked again later.
                parts = null;
            }
            PartModels.Clear();
            PartMatrices.Clear();
            PartNormals.Clear();
            PartPatterns.Clear();
            if (parts != null)
                byOrientation[index] = parts;
            else
                FailedArmor[ArmorKey(definition, index)] = DateTime.UtcNow.AddSeconds(RETRY_SECONDS);
            return parts;
        }

        public void Build()
        {
            _instances.Clear();
            _wireBoxes.Clear();
            _wireEdges = 0;
            SelectBlocks();
            BuildTriangles();
            BuildWires();
        }

        // ---- Selection, on the paint threads -----------------------------------------------------------

        // The visible blocks of every captured grid, nearest first, as instances within the budgets: a model
        // or armor plates for blocks large enough on screen, else (or once the triangles run out) a box.
        void SelectBlocks()
        {
            _items.Clear();
            for (int g = 0; g < _frameGrids.Count; g++)
            {
                AddItems(g, _frameGrids[g].Blocks);
                AddItems(g, _frameGrids[g].CameraBlocks);
            }
            while (_itemCandidates.Count < _items.Count)
                _itemCandidates.Add(new List<Candidate>());
            if (_visibilityItem == null)
                _visibilityItem = FindVisible;
            if (_items.Count == 1)
                FindVisible(0);
            else if (_items.Count > 1)
                PaintWorkers.For(0, _items.Count, _visibilityItem);

            // Nearest first, by distance buckets (a counting sort): exact order within a bucket does not matter.
            Array.Clear(_bucketCounts, 0, _bucketCounts.Length);
            int total = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                List<Candidate> candidates = _itemCandidates[i];
                total += candidates.Count;
                for (int c = 0; c < candidates.Count; c++)
                    _bucketCounts[Bucket(candidates[c].Distance) + 1]++;
            }
            for (int b = 0; b < BUCKET_COUNT; b++)
                _bucketCounts[b + 1] += _bucketCounts[b];
            if (_sorted.Length < total)
                _sorted = new Candidate[Math.Max(total, _sorted.Length * 2)];
            for (int i = 0; i < _items.Count; i++)
            {
                List<Candidate> candidates = _itemCandidates[i];
                for (int c = 0; c < candidates.Count; c++)
                    _sorted[_bucketCounts[Bucket(candidates[c].Distance)]++] = candidates[c];
                candidates.Clear();
            }

            Render3DSettings settings = Render3DSettings.Current;
            int maximumInstances = settings.BlockInstances;
            int maximumTriangles = settings.BlockTriangles;
            int triangles = 0;
            _detailed.Clear();
            _requests.Clear();
            for (int i = 0; i < total && _instances.Count < maximumInstances; i++)
            {
                CachedBlock block = _sorted[i].Block;
                FrameGrid frameGrid = _frameGrids[_sorted[i].Grid];
                MatrixD gridToScene = frameGrid.GridToScene;
                Vector3 paint;
                Vector3 highlight;
                bool flat;
                bool wire;
                bool unlit;
                TryGetHighlight(ref frameGrid, block, out paint, out flat, out unlit, out wire, out highlight);
                if (wire)
                {
                    if (frameGrid.HighlightStyle == GridHighlightStyle.ModelWireframe)
                        AddModelWire(block, gridToScene, highlight, _sorted[i].Pixels >= MODEL_MINIMUM_PIXELS);
                    else if (_wireBoxes.Count < MAXIMUM_WIRE_BOXES)
                        _wireBoxes.Add(new WireBox
                        {
                            ToScene = gridToScene,
                            Minimum = block.BoxMinimum,
                            Maximum = block.BoxMaximum,
                            Color = highlight,
                            SeeThrough = frameGrid.HighlightStyle == GridHighlightStyle.SeeThroughWireframe
                        });
                    if (frameGrid.HighlightedOnly)
                        continue;
                }
                // Armor leaves out the plates its neighbours cover; on the cut those neighbours are gone, and its
                // box closes the section instead of leaving a hole into the ship.
                bool section = block.Asset == null && _cut.Enabled &&
                               _cut.TouchesTrimmedSide(Vector3D.Transform((Vector3D)block.Center, gridToScene),
                                   block.Radius);
                // Armor is always its plates: about a box's triangles, and slopes keep their shape at any range.
                ArmorSet armor = section ? null : block.Armor;
                if (armor != null)
                {
                    if (_instances.Count + armor.Models.Length > maximumInstances)
                        break;
                    for (int p = 0; p < armor.Models.Length; p++)
                    {
                        _instances.Add(new Instance
                        {
                            Model = armor.Models[p],
                            ToScene = armor.Locals[p] * gridToScene,
                            Paint = paint,
                            Flat = flat,
                            Unlit = unlit
                        });
                    }
                    triangles += armor.Triangles;
                    continue;
                }
                bool detailed = triangles < maximumTriangles && _sorted[i].Pixels >= MODEL_MINIMUM_PIXELS;
                BlockModel model = block.Model;
                if (model != null && detailed && triangles + model.TriangleCount <= maximumTriangles)
                {
                    _instances.Add(new Instance
                    {
                        Model = model,
                        ToScene = block.Local * gridToScene,
                        Paint = paint,
                        Flat = flat,
                        Unlit = unlit
                    });
                    triangles += model.TriangleCount;
                    _detailed.Add(block.Key);
                    continue;
                }
                // Armor plates load at any size; a model only once it would be drawn.
                if (block.Pending && !section && (detailed || block.Asset == null) && _requests.Count < MAXIMUM_REQUESTS)
                    _requests.Add(block);
                // A window stays open: its frame is too small to matter here, and a box would hide the view.
                if (block.Glass)
                    continue;
                _instances.Add(new Instance
                {
                    ToScene = gridToScene,
                    Minimum = block.BoxMinimum,
                    Maximum = block.BoxMaximum,
                    Paint = paint,
                    Flat = flat,
                    Unlit = unlit
                });
            }

            for (int i = 0; i < _subparts.Count && _instances.Count < maximumInstances; i++)
            {
                FrameSubpart subpart = _subparts[i];
                if (!_detailed.Contains(subpart.Owner.Key) || triangles + subpart.Model.TriangleCount > maximumTriangles)
                    continue;
                FrameGrid owner = _frameGrids[subpart.Grid];
                Vector3 paint;
                Vector3 highlight;
                bool flat;
                bool wire;
                bool unlit;
                TryGetHighlight(ref owner, subpart.Owner, out paint, out flat, out unlit, out wire, out highlight);
                _instances.Add(new Instance
                {
                    Model = subpart.Model,
                    ToScene = subpart.ToScene,
                    Paint = paint,
                    Flat = flat,
                    Unlit = unlit
                });
                triangles += subpart.Model.TriangleCount;
            }
            if (_requests.Count > 0)
            {
                lock (RequestGate)
                {
                    Requests.Clear();
                    Requests.AddRange(_requests);
                }
            }
            _requests.Clear();
        }

        // The block's model (or armor plates) outlined, once loaded and large enough on screen; its box otherwise.
        void AddModelWire(CachedBlock block, MatrixD gridToScene, Vector3 color, bool large)
        {
            if (_wireBoxes.Count >= MAXIMUM_WIRE_BOXES)
                return;
            ArmorSet armor = block.Armor;
            BlockModel model = block.Model;
            if (large && armor != null && _wireEdges + armor.Triangles <= MAXIMUM_WIRE_EDGES)
            {
                for (int p = 0; p < armor.Models.Length; p++)
                    _wireBoxes.Add(new WireBox
                    {
                        ToScene = armor.Locals[p] * gridToScene,
                        Model = armor.Models[p],
                        Color = color
                    });
                _wireEdges += armor.Triangles;
                return;
            }
            if (large && model != null && _wireEdges + model.TriangleCount <= MAXIMUM_WIRE_EDGES)
            {
                _wireBoxes.Add(new WireBox
                {
                    ToScene = block.Local * gridToScene,
                    Model = model,
                    Color = color
                });
                _wireEdges += model.TriangleCount;
                return;
            }
            if (large && block.Pending && _requests.Count < MAXIMUM_REQUESTS)
                _requests.Add(block);
            _wireBoxes.Add(new WireBox
            {
                ToScene = gridToScene,
                Minimum = block.BoxMinimum,
                Maximum = block.BoxMaximum,
                Color = color
            });
        }

        // The block's fill (flat: the color as is; unlit: not lit either), and whether a wireframe outlines it in the
        // highlight color.
        static void TryGetHighlight(ref FrameGrid grid, CachedBlock block, out Vector3 paint, out bool flat,
            out bool unlit, out bool wire, out Vector3 highlight)
        {
            paint = grid.HasTint ? grid.Tint : block.Paint;
            flat = grid.HasTint;
            unlit = false;
            wire = false;
            if (grid.Highlights == null || !grid.Highlights.TryGetValue(block.Position, out highlight))
            {
                highlight = Vector3.Zero;
                return;
            }
            switch (grid.HighlightStyle)
            {
                case GridHighlightStyle.Solid:
                    paint = highlight;
                    flat = true;
                    break;
                case GridHighlightStyle.Tint:
                    paint = highlight;
                    flat = false;
                    break;
                case GridHighlightStyle.Overlay:
                    paint = highlight;
                    flat = true;
                    unlit = true;
                    break;
                default:
                    wire = true;
                    break;
            }
        }

        static int Bucket(float distance)
        {
            if (!(distance > 1f))
                return 0;
            int bucket = (int)(Math.Log(distance, 2d) * BUCKETS_PER_OCTAVE) + 1;
            return bucket >= BUCKET_COUNT ? BUCKET_COUNT - 1 : bucket;
        }

        void AddItems(int grid, CachedBlock[] blocks)
        {
            if (blocks == null)
                return;
            for (int first = 0; first < blocks.Length; first += VISIBILITY_ITEM_BLOCKS)
                _items.Add(new VisibilityItem
                {
                    Grid = grid,
                    Blocks = blocks,
                    First = first,
                    End = Math.Min(blocks.Length, first + VISIBILITY_ITEM_BLOCKS)
                });
        }

        // One work item of blocks: those on screen, large enough and not the excluded screen block.
        void FindVisible(int index)
        {
            VisibilityItem item = _items[index];
            FrameGrid grid = _frameGrids[item.Grid];
            CachedBlock[] blocks = item.Blocks;
            List<Candidate> candidates = _itemCandidates[index];
            candidates.Clear();
            SceneCamera camera = _camera;
            SceneCut cut = _cut;
            double minimumPixels = _minimumPixels;
            for (int b = item.First; b < item.End; b++)
            {
                CachedBlock block = blocks[b];
                Vector3D center = Vector3D.Transform((Vector3D)block.Center, grid.GridToScene);
                SceneVertex vertex;
                camera.ToCameraSpace(ref center, out vertex);
                double pixels;
                if (!camera.MayBeOnScreen(ref vertex, block.Radius, out pixels) || pixels < minimumPixels)
                    continue;
                if (grid.HighlightedOnly && (grid.Highlights == null || !grid.Highlights.ContainsKey(block.Position)))
                    continue;
                if (cut.Enabled && !cut.Contains(center))
                    continue;
                if (grid.HasExclude)
                {
                    Vector3D cell = grid.ExcludeCell;
                    if (cell.X >= block.Min.X - 0.5d && cell.X <= block.Max.X + 0.5d &&
                        cell.Y >= block.Min.Y - 0.5d && cell.Y <= block.Max.Y + 0.5d &&
                        cell.Z >= block.Min.Z - 0.5d && cell.Z <= block.Max.Z + 0.5d)
                        continue;
                }
                double distance = Math.Sqrt(vertex.X * vertex.X + vertex.Y * vertex.Y + vertex.Z * vertex.Z) -
                                  block.Radius;
                candidates.Add(new Candidate
                {
                    Block = block,
                    Grid = item.Grid,
                    Distance = (float)Math.Max(0d, distance),
                    Pixels = (float)Math.Min(pixels, 1e9d)
                });
            }
        }

        // ---- Triangles, on the paint threads -----------------------------------------------------------

        void BuildTriangles()
        {
            int count = _instances.Count;
            long total = 0;
            for (int i = 0; i < count; i++)
                total += Cost(i);
            int chunks = count == 0
                ? 0
                : (int)Math.Max(1L, Math.Min(Math.Min(count, PaintWorkers.ThreadCount * CHUNKS_PER_THREAD),
                    total / MINIMUM_CHUNK_TRIANGLES));
            while (_chunks.Count < chunks)
                _chunks.Add(new BuildChunk());
            _chunkCount = chunks;
            if (chunks == 0)
                return;

            int instance = 0;
            long done = 0;
            for (int c = 0; c < chunks; c++)
            {
                BuildChunk chunk = _chunks[c];
                chunk.First = instance;
                long target = total * (c + 1) / chunks;
                while (instance < count && (done < target || c == chunks - 1))
                    done += Cost(instance++);
                chunk.End = instance;
                chunk.Failure = null;
            }

            if (chunks == 1)
            {
                BuildRange(_chunks[0]);
            }
            else
            {
                if (_buildChunk == null)
                    _buildChunk = BuildChunkAt;
                PaintWorkers.For(0, chunks, _buildChunk);
            }
            for (int c = 0; c < chunks; c++)
            {
                if (_chunks[c].Failure != null)
                    throw new Exception("Block scene build failed.", _chunks[c].Failure);
            }
        }

        int Cost(int index)
        {
            BlockModel model = _instances[index].Model;
            return model == null ? BOX_TRIANGLES : Math.Max(1, model.TriangleCount);
        }

        void BuildChunkAt(int index)
        {
            BuildChunk chunk = _chunks[index];
            try
            {
                BuildRange(chunk);
            }
            catch (Exception exception)
            {
                // Caught on the worker: the engine may treat uncaught parallel task exceptions as fatal.
                chunk.Failure = exception;
            }
        }

        void BuildRange(BuildChunk chunk)
        {
            SceneTriangleList triangles = chunk.Triangles;
            triangles.Clear();
            for (int i = chunk.First; i < chunk.End; i++)
            {
                Instance instance = _instances[i];
                if (instance.Model == null)
                {
                    BuildBox(chunk, ref instance);
                    continue;
                }

                BlockModel model = instance.Model;
                Vector3[] vertices = model.Vertices;
                if (chunk.Transformed.Length < vertices.Length)
                    chunk.Transformed = new Vector3D[vertices.Length];
                Vector3D[] transformed = chunk.Transformed;
                for (int v = 0; v < vertices.Length; v++)
                    transformed[v] = Vector3D.Transform((Vector3D)vertices[v], instance.ToScene);
                for (int part = 0; part < model.PartIndices.Length; part++)
                {
                    // The paint mask tints the texture the way the game's colorable materials do.
                    Vector3 tint = instance.Flat
                        ? instance.Paint
                        : model.PartTints[part] * (new Vector3(0.30f) + instance.Paint * 1.05f);
                    int[] indices = model.PartIndices[part];
                    for (int t = 0; t + 2 < indices.Length; t += 3)
                    {
                        int a = indices[t];
                        int b = indices[t + 1];
                        int c = indices[t + 2];
                        if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length ||
                            (uint)c >= (uint)vertices.Length)
                            continue;
                        AddTriangle(triangles, ref transformed[a], ref transformed[b], ref transformed[c], tint,
                            instance.Unlit);
                    }
                }
            }
            triangles.Bin(_camera.Height);
        }

        void BuildBox(BuildChunk chunk, ref Instance instance)
        {
            Vector3 min = instance.Minimum;
            Vector3 max = instance.Maximum;
            Vector3D[] corners = chunk.Corners;
            corners[0] = Vector3D.Transform(new Vector3D(min.X, min.Y, min.Z), instance.ToScene);
            corners[1] = Vector3D.Transform(new Vector3D(max.X, min.Y, min.Z), instance.ToScene);
            corners[2] = Vector3D.Transform(new Vector3D(max.X, max.Y, min.Z), instance.ToScene);
            corners[3] = Vector3D.Transform(new Vector3D(min.X, max.Y, min.Z), instance.ToScene);
            corners[4] = Vector3D.Transform(new Vector3D(min.X, min.Y, max.Z), instance.ToScene);
            corners[5] = Vector3D.Transform(new Vector3D(max.X, min.Y, max.Z), instance.ToScene);
            corners[6] = Vector3D.Transform(new Vector3D(max.X, max.Y, max.Z), instance.ToScene);
            corners[7] = Vector3D.Transform(new Vector3D(min.X, max.Y, max.Z), instance.ToScene);
            for (int t = 0; t < BoxTriangles.Length; t += 3)
            {
                AddTriangle(chunk.Triangles, ref corners[BoxTriangles[t]], ref corners[BoxTriangles[t + 1]],
                    ref corners[BoxTriangles[t + 2]], instance.Paint, instance.Unlit);
            }
        }

        void AddTriangle(SceneTriangleList triangles, ref Vector3D a, ref Vector3D b, ref Vector3D c, Vector3 color,
            bool unlit)
        {
            // Two-sided grid lighting: ambient plus a directional term on the face normal.
            Vector3 normal = (Vector3)Vector3D.Cross(b - a, c - a);
            float shade = unlit ? 1f : 0.62f;
            if (!unlit && normal.Normalize() > 1e-9f)
                shade = MathHelper.Clamp(0.64f + 0.34f * Math.Abs(Vector3.Dot(normal, _light)), 0.34f, 1.05f);

            SceneVertex va;
            SceneVertex vb;
            SceneVertex vc;
            _camera.ToCameraSpace(ref a, out va);
            _camera.ToCameraSpace(ref b, out vb);
            _camera.ToCameraSpace(ref c, out vc);
            va.Attribute = vb.Attribute = vc.Attribute = color * shade;
            triangles.Add(ref _camera, ref va, ref vb, ref vc);
        }

        public void Draw(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow)
        {
            if (depthBuffer == null)
                return;
            for (int c = 0; c < _chunkCount; c++)
            {
                SceneTriangleList triangles = _chunks[c].Triangles;
                if (triangles.Count > 0)
                    SceneRasterizer.DrawFlat(canvas, depthBuffer, triangles, firstRow, endRow);
            }
            DrawWires(canvas, depthBuffer, firstRow, endRow, false);
        }

        // The see-through wireframes, over everything drawn before them; a host adds this after its last layer.
        public void DrawSeeThrough(PixelCanvas canvas, int firstRow, int endRow)
        {
            DrawWires(canvas, null, firstRow, endRow, true);
        }

        // ---- Wireframes, on the paint threads ----------------------------------------------------------

        const int MAXIMUM_WIRE_BOXES = 20000;
        // Model outlines, counted by their models' triangles (about as many edges, before the flat ones go).
        const int MAXIMUM_WIRE_EDGES = 150000;
        // Edges between faces meeting flatter than this are left out of a model's outline: a curved surface's
        // triangles would fill it in.
        const float OUTLINE_CREASE_COSINE = 0.94f;
        // A wire on a face lies at that face's depth: it wins against it, and against what lies a little nearer.
        const float WIRE_DEPTH_BIAS_METERS = 0.05f;
        const float WIRE_DEPTH_BIAS_FRACTION = 0.004f;
        static readonly int[] BoxEdges = { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };

        // A box (model null), or a model's outline.
        struct WireBox
        {
            public MatrixD ToScene;
            public BlockModel Model;
            public Vector3 Minimum;
            public Vector3 Maximum;
            public Vector3 Color;
            public bool SeeThrough;
        }

        // In screen pixels, with the inverse camera depth at each end (linear across the screen).
        struct WireSegment
        {
            public float X0;
            public float Y0;
            public float InverseZ0;
            public float X1;
            public float Y1;
            public float InverseZ1;
            public float MinY;
            public float MaxY;
            public Color Color;
            public bool SeeThrough;
        }

        readonly List<WireBox> _wireBoxes = new List<WireBox>();
        int _wireEdges;
        readonly List<SceneLine> _lines = new List<SceneLine>();

        // Game thread, after Capture: lines in scene coordinates, seen through everything (markers).
        public void SetLines(List<SceneLine> lines)
        {
            _lines.Clear();
            if (lines != null)
                _lines.AddRange(lines);
        }
        readonly List<WireSegment> _wires = new List<WireSegment>();
        readonly Vector3D[] _wireCorners = new Vector3D[8];

        void BuildWires()
        {
            _wires.Clear();
            Vector3D[] corners = _wireCorners;
            for (int w = 0; w < _wireBoxes.Count; w++)
            {
                WireBox box = _wireBoxes[w];
                if (box.Model != null)
                {
                    AddModelEdges(ref box);
                    continue;
                }
                Vector3 min = box.Minimum;
                Vector3 max = box.Maximum;
                corners[0] = Vector3D.Transform(new Vector3D(min.X, min.Y, min.Z), box.ToScene);
                corners[1] = Vector3D.Transform(new Vector3D(max.X, min.Y, min.Z), box.ToScene);
                corners[2] = Vector3D.Transform(new Vector3D(max.X, max.Y, min.Z), box.ToScene);
                corners[3] = Vector3D.Transform(new Vector3D(min.X, max.Y, min.Z), box.ToScene);
                corners[4] = Vector3D.Transform(new Vector3D(min.X, min.Y, max.Z), box.ToScene);
                corners[5] = Vector3D.Transform(new Vector3D(max.X, min.Y, max.Z), box.ToScene);
                corners[6] = Vector3D.Transform(new Vector3D(max.X, max.Y, max.Z), box.ToScene);
                corners[7] = Vector3D.Transform(new Vector3D(min.X, max.Y, max.Z), box.ToScene);
                var color = new Color(box.Color);
                for (int e = 0; e < BoxEdges.Length; e += 2)
                    AddWire(ref corners[BoxEdges[e]], ref corners[BoxEdges[e + 1]], color, box.SeeThrough);
            }
            for (int i = 0; i < _lines.Count; i++)
            {
                SceneLine line = _lines[i];
                AddWire(ref line.From, ref line.To, line.Color, true);
            }
        }

        void AddModelEdges(ref WireBox box)
        {
            Vector3[] edges = box.Model.Edges;
            if (edges == null)
            {
                // Paint threads may build the same outline at once; either result is the same.
                edges = FindEdges(box.Model);
                box.Model.Edges = edges;
            }
            var color = new Color(box.Color);
            for (int e = 0; e + 1 < edges.Length; e += 2)
            {
                Vector3D a = Vector3D.Transform((Vector3D)edges[e], box.ToScene);
                Vector3D b = Vector3D.Transform((Vector3D)edges[e + 1], box.ToScene);
                AddWire(ref a, ref b, color, false);
            }
        }

        struct OutlineEdge
        {
            public int A;
            public int B;
            public Vector3 Normal;
            public int Faces;
            public bool Crease;
        }

        // The edges that shape a model: where its faces fold, and its open borders. Corners are merged by
        // position first, since a model repeats them for each face's normal and texture.
        static Vector3[] FindEdges(BlockModel model)
        {
            Vector3[] vertices = model.Vertices;
            var corners = new Dictionary<Vector3I, int>();
            var points = new List<Vector3>();
            var ids = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                var key = new Vector3I((int)Math.Round(v.X * 1000f), (int)Math.Round(v.Y * 1000f),
                    (int)Math.Round(v.Z * 1000f));
                int id;
                if (!corners.TryGetValue(key, out id))
                {
                    id = points.Count;
                    corners[key] = id;
                    points.Add(v);
                }
                ids[i] = id;
            }
            var edges = new Dictionary<long, OutlineEdge>();
            for (int part = 0; part < model.PartIndices.Length; part++)
            {
                int[] indices = model.PartIndices[part];
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int a = ids[indices[t]];
                    int b = ids[indices[t + 1]];
                    int c = ids[indices[t + 2]];
                    if (a == b || b == c || a == c)
                        continue;
                    Vector3 normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                    if (normal.LengthSquared() < 1e-12f)
                        continue;
                    normal.Normalize();
                    AddFace(edges, a, b, normal);
                    AddFace(edges, b, c, normal);
                    AddFace(edges, c, a, normal);
                }
            }
            var result = new List<Vector3>();
            foreach (OutlineEdge edge in edges.Values)
            {
                if (edge.Faces != 2 || edge.Crease)
                {
                    result.Add(points[edge.A]);
                    result.Add(points[edge.B]);
                }
            }
            return result.ToArray();
        }

        static void AddFace(Dictionary<long, OutlineEdge> edges, int a, int b, Vector3 normal)
        {
            int low = Math.Min(a, b);
            int high = Math.Max(a, b);
            long key = ((long)low << 32) | (uint)high;
            OutlineEdge edge;
            if (!edges.TryGetValue(key, out edge))
            {
                edges[key] = new OutlineEdge { A = low, B = high, Normal = normal, Faces = 1 };
                return;
            }
            edge.Faces++;
            if (Vector3.Dot(edge.Normal, normal) < OUTLINE_CREASE_COSINE)
                edge.Crease = true;
            edges[key] = edge;
        }

        void AddWire(ref Vector3D a, ref Vector3D b, Color color, bool seeThrough)
        {
            SceneVertex va;
            SceneVertex vb;
            _camera.ToCameraSpace(ref a, out va);
            _camera.ToCameraSpace(ref b, out vb);
            double near = _camera.NearDepth;
            if (va.Z <= near && vb.Z <= near)
                return;
            if (va.Z <= near || vb.Z <= near)
            {
                // Only the part in front of the near plane is drawn.
                double t = (near - va.Z) / (vb.Z - va.Z);
                var cut = new SceneVertex
                {
                    X = va.X + (vb.X - va.X) * t,
                    Y = va.Y + (vb.Y - va.Y) * t,
                    Z = near
                };
                if (va.Z <= near)
                    va = cut;
                else
                    vb = cut;
            }
            double focal = _camera.FocalLengthPixels;
            var segment = new WireSegment
            {
                X0 = (float)(_camera.CenterX + focal * va.X / va.Z),
                Y0 = (float)(_camera.CenterY - focal * va.Y / va.Z),
                InverseZ0 = (float)(1d / va.Z),
                X1 = (float)(_camera.CenterX + focal * vb.X / vb.Z),
                Y1 = (float)(_camera.CenterY - focal * vb.Y / vb.Z),
                InverseZ1 = (float)(1d / vb.Z),
                Color = color,
                SeeThrough = seeThrough
            };
            segment.MinY = Math.Min(segment.Y0, segment.Y1);
            segment.MaxY = Math.Max(segment.Y0, segment.Y1);
            if (segment.MaxY < -1f || segment.MinY > _camera.Height + 1f ||
                Math.Max(segment.X0, segment.X1) < -1f || Math.Min(segment.X0, segment.X1) > _camera.Width + 1f)
                return;
            _wires.Add(segment);
        }

        void DrawWires(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow, bool seeThrough)
        {
            int width = canvas.Width;
            int end = Math.Min(canvas.Height, endRow);
            for (int i = 0; i < _wires.Count; i++)
            {
                WireSegment wire = _wires[i];
                if (wire.SeeThrough != seeThrough || wire.MaxY < firstRow - 0.5f || wire.MinY >= end - 0.5f)
                    continue;
                float dx = wire.X1 - wire.X0;
                float dy = wire.Y1 - wire.Y0;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));
                // Only the steps that land in this band's rows.
                int first = 0;
                int last = steps;
                if (Math.Abs(dy) > 0.001f)
                {
                    float t0 = (firstRow - 0.5f - wire.Y0) / dy;
                    float t1 = (end - 0.5f - wire.Y0) / dy;
                    first = Math.Max(0, (int)Math.Floor(Math.Min(t0, t1) * steps) - 1);
                    last = Math.Min(steps, (int)Math.Ceiling(Math.Max(t0, t1) * steps) + 1);
                }
                for (int s = first; s <= last; s++)
                {
                    float t = s / (float)steps;
                    int y = (int)Math.Round(wire.Y0 + dy * t);
                    if (y < firstRow || y >= end)
                        continue;
                    int x = (int)Math.Round(wire.X0 + dx * t);
                    if (x < 0 || x >= width)
                        continue;
                    if (depthBuffer != null)
                    {
                        float inverseZ = wire.InverseZ0 + (wire.InverseZ1 - wire.InverseZ0) * t;
                        if (!(inverseZ > 0f))
                            continue;
                        float depth = 1f / inverseZ;
                        // The buffer holds negated depths: larger is nearer.
                        if (-depth + WIRE_DEPTH_BIAS_METERS + depth * WIRE_DEPTH_BIAS_FRACTION < depthBuffer[y * width + x])
                            continue;
                    }
                    canvas.BlendPixel(x, y, wire.Color, 1f);
                }
            }
        }

        static BlockModel ResolveModel(string assetName, ref int loads)
        {
            return MeshAssets.Resolve(assetName, ref loads);
        }
    }
}
