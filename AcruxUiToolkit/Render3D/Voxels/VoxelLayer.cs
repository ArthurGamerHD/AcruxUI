using System;
using System.Collections.Generic;
using Acrux.Rendering;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace Acrux.Render3D.Voxels
{
    /// <summary>
    /// Voxel maps that are not planets (asteroids, and the boulders a planet's environment spawns) as smooth
    /// meshes, a layer of the 3D scene sharing its depth buffer. Each map has an LOD octree like the game's
    /// clipmap: nodes of a fixed cell count, read at their level of the voxel octree and meshed with surface
    /// nets, split where the camera needs about one cell per few pixels, so the part of a large map beside the
    /// camera is full resolution; node meshes are cached and rebuilt when the map's voxels change (mining).
    /// <para>
    /// <see cref="Capture"/> only reads entities on the caller (game) thread and queues mesh work: reading a
    /// storage can generate procedural asteroid voxels on demand, which takes far too long for a game tick, so
    /// one background task at a time reads and meshes a map and hands the mesh back on the game thread.
    /// <see cref="Build"/> transforms and projects on the paint coordinator, <see cref="Draw"/> runs on the
    /// row-band workers.
    /// </para>
    /// </summary>
    internal sealed class VoxelLayer : ISceneLayer
    {
        // The triangle budget, background jobs and cell size: Render3DSettings (AcruxSettings.xml).
        // Cells along each side of an octree node's mesh, at the node's level: a level-L node spans
        // NODE_CELLS << L voxels.
        const int NODE_CELLS = 16;
        const int MAXIMUM_LEVEL = 8;
        const double EVICT_SECONDS = 60d;
        const byte ISO_LEVEL = 127;
        const double RETRY_SECONDS = 20d;
        // Split hysteresis, in levels: a node splits once the ideal level is this far below its own and merges
        // back only once it is within MERGE_AT, so a moving camera does not flip nodes at a level boundary.
        const double SPLIT_AT = 0.7d;
        const double MERGE_AT = 0.3d;

        sealed class VoxelMesh
        {
            public int Level;
            // Map-local metres, relative to the storage centre (the entity's translation).
            public Vector3[] Positions;
            public Vector3[] Normals;
            public byte[] Materials;
            public int[] Indices;
        }

        // One node of a map's LOD octree, as the game's clipmap does: a cube of the storage meshed with
        // NODE_CELLS cells per side at its level, split into eight children of the next finer level where the
        // camera needs more detail, so the part of a large map beside the camera is full resolution and its far
        // end stays coarse.
        sealed class Chunk
        {
            public VoxelMesh Mesh;
            // Splitting is wanted (kept with hysteresis).
            public bool Split;
            // The octree reported the node all empty or all solid at full resolution: no surface at any level.
            public bool Uniform;
            public DateTime Seen;
            // The map version the mesh was read at; older than the map's means a change since.
            public int BuiltVersion = -1;
            // After a failed read: not before this time.
            public DateTime RetryAt;
        }

        // One voxel map's chunks. Its voxels changing (mining, a voxel hand, a drill) bumps its version, so
        // chunks are rebuilt when the map changes rather than on a timer.
        sealed class Entry
        {
            public readonly Dictionary<long, Chunk> Chunks = new Dictionary<long, Chunk>();
            public DateTime Seen;
            public MyVoxelBase Voxel;
            public IMyStorage Storage;
            public MyVoxelBase.StorageChanged Handler;
            // Set from whichever thread changes the storage.
            public volatile int Version;
        }

        struct Instance
        {
            public VoxelMesh Mesh;
            public MatrixD ToScene;
        }

        struct ChunkKey : IEquatable<ChunkKey>
        {
            public long Entity;
            public long Chunk;

            public bool Equals(ChunkKey other)
            {
                return Entity == other.Entity && Chunk == other.Chunk;
            }

            public override bool Equals(object obj)
            {
                return obj is ChunkKey && Equals((ChunkKey)obj);
            }

            public override int GetHashCode()
            {
                return Entity.GetHashCode() * 397 ^ Chunk.GetHashCode();
            }
        }

        // A background read-and-mesh of one chunk. Written by the task, read by its completion callback.
        sealed class MeshJob
        {
            public ChunkKey Key;
            public IMyStorage Storage;
            public Vector3I Size;
            // The chunk's lod-0 voxel range, inclusive minimum, exclusive maximum.
            public Vector3I Min;
            public Vector3I Max;
            public int Level;
            public int Version;
            public int Generation;
            public VoxelMesh Result;
            public bool Failed;
            public bool Uniform;
            // 0 a node with no mesh yet, 1 a finer node to refine into, 2 a changed map.
            public int Priority;
            public double Pixels;
        }

        // Game thread only.
        static readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        static readonly Dictionary<ChunkKey, MeshJob> Running = new Dictionary<ChunkKey, MeshJob>();
        static readonly List<long> Expired = new List<long>();
        static readonly List<long> ExpiredNodes = new List<long>();
        static readonly List<MeshJob> Candidates = new List<MeshJob>();
        // The last capture's candidates, best first, still to start: a finishing job starts the next one at
        // once instead of the pool idling until the screen's next capture.
        static readonly List<MeshJob> Pending = new List<MeshJob>();
        static int _pendingNext;
        static int _generation;

        readonly List<IMyEntity> _entities = new List<IMyEntity>();
        readonly List<Instance> _instances = new List<Instance>();
        readonly SceneTriangleList _triangles = new SceneTriangleList();
        Vector3D[] _transformed = new Vector3D[0];
        Vector3[] _lit = new Vector3[0];
        Vector3[] _normals = new Vector3[0];
        SceneCamera _camera;
        Vector3 _light;

        public static bool IsBusy
        {
            get { return Running.Count > 0; }
        }

        readonly IVoxelMaterialColors _colors;

        public VoxelLayer(IVoxelMaterialColors colors)
        {
            _colors = colors;
        }

        // A chunk shown with an older mesh (another level, or before a change) does not count: that scene is
        // complete, only being refined.
        public bool Incomplete { get; private set; }

        public string PendingWork
        {
            get
            {
                if (Incomplete)
                    return "voxels";
                return _colors != null && _colors.IsLoading ? "voxel colors" : null;
            }
        }

        internal static void ReleaseShared()
        {
            // Tasks still running finish into a generation nobody reads.
            _generation++;
            Running.Clear();
            Pending.Clear();
            _pendingNext = 0;
            foreach (Entry entry in Entries.Values)
                Unsubscribe(entry);
            Entries.Clear();
        }

        public void Capture(ref SceneCapture capture)
        {
            HashSet<long> only = capture.Only;
            SceneCamera camera = capture.Camera;
            MatrixD worldToScene = capture.WorldToScene;
            Vector3D centerWorld = capture.CenterWorld;
            double radius = capture.Radius;
            _camera = camera;
            _light = capture.Light;
            _instances.Clear();
            _entities.Clear();
            Incomplete = false;
            if (MyAPIGateway.Entities == null || MyDefinitionManager.Static == null)
                return;
            if (_colors != null)
                _colors.EnsureStarted();

            DateTime now = DateTime.UtcNow;
            var sphere = new BoundingSphereD(centerWorld, radius);
            _entities.AddRange(MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere));
            // Nearest maps first: if the triangle budget still runs out, it is the far ones that go missing.
            _entities.Sort((a, b) => a.WorldAABB.DistanceSquared(centerWorld)
                .CompareTo(b.WorldAABB.DistanceSquared(centerWorld)));
            int triangles = 0;
            _wantedTriangles = 0;
            _selected.Clear();
            Candidates.Clear();
            for (int e = 0; e < _entities.Count; e++)
            {
                var voxel = _entities[e] as IMyVoxelBase;
                if (voxel == null || voxel is MyPlanet || voxel.MarkedForClose || voxel.Closed ||
                    (only != null && !only.Contains(voxel.EntityId)))
                    continue;
                // A planet's physics chunks (MyVoxelPhysics) are voxel entities of their own over the planet's
                // storage: reading them generates the planet's terrain, which the terrain scene already draws.
                // Boulders and asteroids are their own root.
                var concrete = voxel as MyVoxelBase;
                if (concrete != null && !ReferenceEquals(concrete.RootVoxel, concrete))
                    continue;
                IMyStorage storage = voxel.Storage;
                if (storage == null || storage.Closed || storage.MarkedForClose)
                    continue;
                Vector3I size = storage.Size;
                int largest = Math.Max(size.X, Math.Max(size.Y, size.Z));
                if (largest <= 0)
                    continue;

                MatrixD toScene = voxel.WorldMatrix * worldToScene;
                Vector3D centerScene = toScene.Translation;
                SceneVertex centerCamera;
                camera.ToCameraSpace(ref centerScene, out centerCamera);
                double onScreenPixels;
                // Entirely behind the camera, or off the sides of the screen: not drawn, so not meshed.
                if (!camera.MayBeOnScreen(ref centerCamera, largest * 0.87d, out onScreenPixels))
                    continue;

                Entry entry = GetEntry(voxel);
                entry.Seen = now;
                // The map got another storage (reloaded, replaced): nothing cached belongs to it any more.
                if (!ReferenceEquals(entry.Storage, storage))
                {
                    entry.Storage = storage;
                    entry.Chunks.Clear();
                    entry.Version++;
                }
                // Roots: the coarsest nodes, one covering the whole storage unless it is beyond MAXIMUM_LEVEL.
                int rootLevel = 0;
                while ((NODE_CELLS << rootLevel) < largest && rootLevel < MAXIMUM_LEVEL)
                    rootLevel++;
                int rootSide = NODE_CELLS << rootLevel;
                _node = new NodeContext
                {
                    Entry = entry,
                    EntityId = voxel.EntityId,
                    Storage = storage,
                    Size = size,
                    Half = new Vector3D(size) * 0.5d,
                    ToScene = toScene,
                    Version = entry.Version,
                    Now = now
                };
                for (int rz = 0; rz * rootSide < size.Z; rz++)
                for (int ry = 0; ry * rootSide < size.Y; ry++)
                for (int rx = 0; rx * rootSide < size.X; rx++)
                    SelectNode(rootLevel, new Vector3I(rx, ry, rz));
                _node.Entry = null;
                _node.Storage = null;
            }
            _entities.Clear();
            AdaptDetail();
            // Nearest meshes first: when the budget runs out, the farthest are left out.
            _selected.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            int budget = Render3DSettings.Current.VoxelTriangles;
            for (int i = 0; i < _selected.Count; i++)
            {
                int meshTriangles = _selected[i].Instance.Mesh.Indices.Length / 3;
                if (triangles + meshTriangles > budget)
                    continue;
                triangles += meshTriangles;
                _instances.Add(_selected[i].Instance);
            }
            _selected.Clear();

            // Missing meshes first, then the nodes covering the most screen.
            Candidates.Sort((a, b) => a.Priority != b.Priority
                ? a.Priority.CompareTo(b.Priority)
                : b.Pixels.CompareTo(a.Pixels));
            Pending.Clear();
            Pending.AddRange(Candidates);
            _pendingNext = 0;
            Candidates.Clear();
            StartPending();
            Evict(now);
        }

        struct NodeContext
        {
            public Entry Entry;
            public long EntityId;
            public IMyStorage Storage;
            public Vector3I Size;
            public Vector3D Half;
            public MatrixD ToScene;
            public int Version;
            public DateTime Now;
        }

        NodeContext _node;

        // The walk's chosen meshes, before the budget: kept nearest first when it runs out.
        struct Selected
        {
            public Instance Instance;
            public double Distance;
        }

        readonly List<Selected> _selected = new List<Selected>();

        // Pixels-per-cell multiplier: raised while the selection wants more triangles than the budget, so
        // detail drops evenly everywhere instead of whole maps being skipped, and lowered back once it fits.
        double _detailScale = 1d;
        int _wantedTriangles;
        const double MAXIMUM_DETAIL_SCALE = 16d;

        void AdaptDetail()
        {
            int budget = Render3DSettings.Current.VoxelTriangles;
            if (_wantedTriangles > budget)
                _detailScale = Math.Min(MAXIMUM_DETAIL_SCALE, _detailScale * 1.25d);
            else if (_wantedTriangles < budget * 0.6d)
                _detailScale = Math.Max(1d, _detailScale / 1.1d);
        }

        static long NodeKey(int level, Vector3I c)
        {
            // 19 bits per coordinate (node coordinates are never negative), the level above them.
            const long mask = (1L << 19) - 1;
            return ((long)level << 57) | ((c.Z & mask) << 38) | ((c.Y & mask) << 19) | (c.X & mask);
        }

        // A node's lod-0 voxel range, clipped to the storage; false when it lies outside the storage.
        bool NodeBounds(int level, Vector3I c, out Vector3I min, out Vector3I max)
        {
            int side = NODE_CELLS << level;
            min = c * side;
            max = Vector3I.Min(min + new Vector3I(side), _node.Size);
            return min.X < _node.Size.X && min.Y < _node.Size.Y && min.Z < _node.Size.Z;
        }

        // On screen, with its ideal level (fractional) and covered pixels; false when culled.
        bool NodeView(Vector3I min, Vector3I max, out double ideal, out double pixels)
        {
            double distance;
            return NodeView(min, max, out ideal, out pixels, out distance);
        }

        bool NodeView(Vector3I min, Vector3I max, out double ideal, out double pixels, out double nearest)
        {
            ideal = 0d;
            pixels = 0d;
            nearest = 0d;
            Vector3D local = (new Vector3D(min) + new Vector3D(max)) * 0.5d - _node.Half;
            Vector3D scene = Vector3D.Transform(local, _node.ToScene);
            SceneVertex view;
            _camera.ToCameraSpace(ref scene, out view);
            double extent = (new Vector3D(max) - new Vector3D(min)).Length() * 0.5d;
            double radiusPixels;
            if (!_camera.MayBeOnScreen(ref view, extent, out radiusPixels))
                return false;
            // The nearest the node can be; inside it, as near as a voxel.
            nearest = Math.Max(0d, Math.Sqrt(view.X * view.X + view.Y * view.Y + view.Z * view.Z) - extent);
            double distance = Math.Max(1d, view.Z - extent);
            double voxelPixels = _camera.FocalLengthPixels / distance;
            pixels = (max.X - min.X) * voxelPixels;
            ideal = Math.Max(0d,
                Math.Log(Render3DSettings.Current.VoxelPixelsPerCell * _detailScale / voxelPixels, 2d));
            return true;
        }

        Chunk GetChunk(long key)
        {
            Chunk chunk;
            if (!_node.Entry.Chunks.TryGetValue(key, out chunk))
            {
                chunk = new Chunk();
                _node.Entry.Chunks[key] = chunk;
            }
            chunk.Seen = _node.Now;
            return chunk;
        }

        // Draws a node, or its children where the camera needs them finer and they are ready; queues the meshes
        // missing on the way. A node stays drawn until all of its visible children have meshes, so splitting
        // never opens holes.
        void SelectNode(int level, Vector3I c)
        {
            Vector3I min, max;
            if (!NodeBounds(level, c, out min, out max))
                return;
            double ideal, pixels, nearest;
            if (!NodeView(min, max, out ideal, out pixels, out nearest))
                return;
            long key = NodeKey(level, c);
            Chunk chunk = GetChunk(key);
            if (chunk.Uniform)
                return;

            chunk.Split = level > 0 && ideal < level - (chunk.Split ? MERGE_AT : SPLIT_AT);
            if (chunk.Split)
            {
                bool ready = true;
                for (int i = 0; i < 8; i++)
                {
                    var child = new Vector3I(c.X * 2 + (i & 1), c.Y * 2 + ((i >> 1) & 1), c.Z * 2 + (i >> 2));
                    Vector3I childMin, childMax;
                    double childIdeal, childPixels;
                    if (!NodeBounds(level - 1, child, out childMin, out childMax) ||
                        !NodeView(childMin, childMax, out childIdeal, out childPixels))
                        continue;
                    long childKey = NodeKey(level - 1, child);
                    Chunk childChunk = GetChunk(childKey);
                    if (childChunk.Uniform || childChunk.Mesh != null)
                        continue;
                    ready = false;
                    // Refinement: wanted, but this node's coarser mesh covers it meanwhile. Without a mesh of
                    // its own the node is meshed first instead (below): one coarse read shows the whole node
                    // long before its finer descendants, so a map loads coarse-to-fine, not leaf by leaf.
                    if (chunk.Mesh != null)
                        Queue(childKey, childChunk, level - 1, childMin, childMax, 1, childPixels);
                }
                if (ready)
                {
                    for (int i = 0; i < 8; i++)
                        SelectNode(level - 1,
                            new Vector3I(c.X * 2 + (i & 1), c.Y * 2 + ((i >> 1) & 1), c.Z * 2 + (i >> 2)));
                    return;
                }
            }

            if (chunk.Mesh == null)
            {
                // A node whose read failed waits before the next try; it does not hold the scene "loading".
                if (_node.Now >= chunk.RetryAt)
                {
                    Incomplete = true;
                    Queue(key, chunk, level, min, max, 0, pixels);
                }
                return;
            }
            if (chunk.BuiltVersion != _node.Version)
                Queue(key, chunk, level, min, max, 2, pixels);
            VoxelMesh mesh = chunk.Mesh;
            if (mesh.Indices.Length == 0)
                return;
            _wantedTriangles += mesh.Indices.Length / 3;
            _selected.Add(new Selected
            {
                Instance = new Instance { Mesh = mesh, ToScene = _node.ToScene },
                Distance = nearest
            });
        }

        void Queue(long key, Chunk chunk, int level, Vector3I min, Vector3I max, int priority, double pixels)
        {
            if (_node.Now < chunk.RetryAt)
                return;
            var jobKey = new ChunkKey { Entity = _node.EntityId, Chunk = key };
            if (Running.ContainsKey(jobKey))
                return;
            Candidates.Add(new MeshJob
            {
                Key = jobKey,
                Storage = _node.Storage,
                Size = _node.Size,
                Min = min,
                Max = max,
                Level = level,
                Version = _node.Version,
                Generation = _generation,
                Priority = priority,
                Pixels = pixels
            });
        }

        static Entry GetEntry(IMyVoxelBase voxel)
        {
            Entry entry;
            if (Entries.TryGetValue(voxel.EntityId, out entry))
                return entry;
            entry = new Entry();
            var concrete = voxel as MyVoxelBase;
            if (concrete != null)
            {
                Entry captured = entry;
                entry.Voxel = concrete;
                entry.Handler = (map, minimum, maximum, data) => captured.Version++;
                concrete.RangeChanged += entry.Handler;
            }
            Entries[voxel.EntityId] = entry;
            return entry;
        }

        static void Unsubscribe(Entry entry)
        {
            if (entry.Voxel != null && entry.Handler != null)
                entry.Voxel.RangeChanged -= entry.Handler;
            entry.Voxel = null;
            entry.Handler = null;
        }

        static void Start(MeshJob job)
        {
            Running[job.Key] = job;
            try
            {
                MyAPIGateway.Parallel.StartBackground(
                    () => job.Result = BuildMesh(job, out job.Failed),
                    () => Finish(job));
            }
            catch (Exception)
            {
                Running.Remove(job.Key);
            }
        }

        static void StartPending()
        {
            while (Running.Count < Render3DSettings.Current.VoxelJobs && _pendingNext < Pending.Count)
            {
                MeshJob job = Pending[_pendingNext++];
                if (job.Generation == _generation && !Running.ContainsKey(job.Key))
                    Start(job);
            }
            if (_pendingNext >= Pending.Count)
            {
                Pending.Clear();
                _pendingNext = 0;
            }
        }

        // Game thread: the task's completion callback.
        static void Finish(MeshJob job)
        {
            MeshJob running;
            if (!Running.TryGetValue(job.Key, out running) || !ReferenceEquals(running, job))
                return;
            Running.Remove(job.Key);
            Store(job);
            StartPending();
        }

        static void Store(MeshJob job)
        {
            Entry entry;
            Chunk chunk;
            if (job.Generation != _generation || !Entries.TryGetValue(job.Key.Entity, out entry) ||
                !ReferenceEquals(entry.Storage, job.Storage) ||
                !entry.Chunks.TryGetValue(job.Key.Chunk, out chunk))
                return;
            // A failed read (a storage still loading at startup, or closing) keeps any mesh shown and is tried
            // again later, rather than stored as empty until the map happens to change.
            if (job.Failed)
            {
                chunk.RetryAt = DateTime.UtcNow.AddSeconds(RETRY_SECONDS);
                return;
            }
            chunk.Mesh = job.Result;
            chunk.BuiltVersion = job.Version;
            chunk.Uniform = job.Uniform;
        }

        static void Evict(DateTime now)
        {
            Expired.Clear();
            foreach (KeyValuePair<long, Entry> pair in Entries)
            {
                if ((now - pair.Value.Seen).TotalSeconds > EVICT_SECONDS)
                {
                    Expired.Add(pair.Key);
                    continue;
                }
                // Nodes the camera stopped walking to (merged away, or off screen) free their meshes.
                ExpiredNodes.Clear();
                foreach (KeyValuePair<long, Chunk> node in pair.Value.Chunks)
                {
                    if ((now - node.Value.Seen).TotalSeconds > EVICT_SECONDS)
                        ExpiredNodes.Add(node.Key);
                }
                for (int i = 0; i < ExpiredNodes.Count; i++)
                    pair.Value.Chunks.Remove(ExpiredNodes[i]);
            }
            ExpiredNodes.Clear();
            for (int i = 0; i < Expired.Count; i++)
            {
                Unsubscribe(Entries[Expired[i]]);
                Entries.Remove(Expired[i]);
            }
            Expired.Clear();
        }

        // Surface nets: one vertex per cell the surface crosses, at the mean of its edge crossings, and a quad around
        // every sample edge the surface crosses.
        static VoxelMesh BuildMesh(MeshJob job, out bool failed)
        {
            failed = false;
            IMyStorage storage = job.Storage;
            int level = job.Level;
            Vector3I size = job.Size;
            var empty = new VoxelMesh
            {
                Level = level,
                Positions = new Vector3[0],
                Normals = new Vector3[0],
                Materials = new byte[0],
                Indices = new int[0]
            };

            // Lod-level samples of the whole storage and of the chunk's owned range [own0, own1).
            var samples = new Vector3I(((size.X - 1) >> level) + 1, ((size.Y - 1) >> level) + 1,
                ((size.Z - 1) >> level) + 1);
            Vector3I own0 = new Vector3I(job.Min.X >> level, job.Min.Y >> level, job.Min.Z >> level);
            Vector3I own1 = Vector3I.Min(new Vector3I(
                ((job.Max.X - 1) >> level) + 1, ((job.Max.Y - 1) >> level) + 1, ((job.Max.Z - 1) >> level) + 1), samples);
            // Edges entering the storage from its outside belong to the first chunk along each axis.
            if (own0.X == 0) own0.X = -1;
            if (own0.Y == 0) own0.Y = -1;
            if (own0.Z == 0) own0.Z = -1;
            // Samples needed: one before the owned range to one past it, within the storage.
            Vector3I read0 = Vector3I.Max(own0 - 1, Vector3I.Zero);
            Vector3I read1 = Vector3I.Min(own1, samples - 1);
            if (read1.X < read0.X || read1.Y < read0.Y || read1.Z < read0.Z)
                return empty;

            var data = new MyStorageData();
            try
            {
                if (storage.Closed || storage.MarkedForClose)
                {
                    failed = true;
                    return null;
                }
                // The octree answers "all empty" or "all solid" without generating anything: no surface.
                int margin = 1 << level;
                var box = new BoundingBox(new Vector3(read0 * margin) - margin, new Vector3((read1 + 1) * margin) + margin);
                ContainmentType content = storage.Intersect(ref box, true);
                if (content == ContainmentType.Disjoint || content == ContainmentType.Contains)
                {
                    job.Uniform = true;
                    return empty;
                }
                data.Resize(read0, read1);
                // Pinned, the storage cannot be closed under the read.
                storage.PinAndExecute(() =>
                    storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, level, read0, read1));
            }
            catch (Exception)
            {
                // A storage closing under us, or one that cannot serve this level: draw nothing for now.
                failed = true;
                return null;
            }

            // Cells, by their minimum corner sample, from own0 - 1 to own1 - 1.
            Vector3I cellMin = own0 - 1;
            Vector3I cells = own1 - own0 + 1;
            var cellVertex = new int[cells.X * cells.Y * cells.Z];
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var materials = new List<byte>();
            var indices = new List<int>();
            float step = 1 << level;
            // Level samples sit where the game's own mesher puts them: sample p at p * 2^level voxels from the
            // storage corner (MyIsoMesh: PositionScale = 1 << lod).
            Vector3 half = new Vector3(size) * 0.5f;
            var corner = new float[8];

            for (int z = cellMin.Z; z < own1.Z; z++)
            for (int y = cellMin.Y; y < own1.Y; y++)
            for (int x = cellMin.X; x < own1.X; x++)
            {
                int cell = (x - cellMin.X) + cells.X * ((y - cellMin.Y) + cells.Y * (z - cellMin.Z));
                cellVertex[cell] = -1;
                int inside = 0;
                for (int i = 0; i < 8; i++)
                {
                    corner[i] = Content(data, read0, samples, x + (i & 1), y + ((i >> 1) & 1), z + (i >> 2));
                    if (corner[i] > ISO_LEVEL)
                        inside++;
                }
                if (inside == 0 || inside == 8)
                    continue;

                Vector3 sum = Vector3.Zero;
                int crossings = 0;
                for (int a = 0; a < 8; a++)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int b = a | (1 << axis);
                        if (b == a)
                            continue;
                        bool aInside = corner[a] > ISO_LEVEL;
                        bool bInside = corner[b] > ISO_LEVEL;
                        if (aInside == bInside)
                            continue;
                        float t = (ISO_LEVEL + 0.5f - corner[a]) / (corner[b] - corner[a]);
                        var pa = new Vector3(a & 1, (a >> 1) & 1, a >> 2);
                        var pb = new Vector3(b & 1, (b >> 1) & 1, b >> 2);
                        sum += pa + (pb - pa) * t;
                        crossings++;
                    }
                }
                Vector3 local = new Vector3(x, y, z) + sum / crossings;

                // Content falls toward the outside; its negative gradient is the outward normal.
                Vector3 gradient = new Vector3(
                    corner[1] + corner[3] + corner[5] + corner[7] - corner[0] - corner[2] - corner[4] - corner[6],
                    corner[2] + corner[3] + corner[6] + corner[7] - corner[0] - corner[1] - corner[4] - corner[5],
                    corner[4] + corner[5] + corner[6] + corner[7] - corner[0] - corner[1] - corner[2] - corner[3]);
                Vector3 normal = -gradient;
                if (normal.Normalize() <= 1e-6f)
                    normal = Vector3.Up;

                // The fullest corner's material colors the vertex.
                int fullest = 0;
                for (int i = 1; i < 8; i++)
                {
                    if (corner[i] > corner[fullest])
                        fullest = i;
                }
                byte material = Material(data, read0, read1, x + (fullest & 1), y + ((fullest >> 1) & 1),
                    z + (fullest >> 2));

                cellVertex[cell] = positions.Count;
                positions.Add(local * step - half);
                normals.Add(normal);
                materials.Add(material);
            }

            // A crossed sample edge from (x,y,z) along an axis is shared by the four cells around it.
            for (int z = own0.Z; z < own1.Z; z++)
            for (int y = own0.Y; y < own1.Y; y++)
            for (int x = own0.X; x < own1.X; x++)
            {
                bool here = Content(data, read0, samples, x, y, z) > ISO_LEVEL;
                for (int axis = 0; axis < 3; axis++)
                {
                    int ex = x + (axis == 0 ? 1 : 0);
                    int ey = y + (axis == 1 ? 1 : 0);
                    int ez = z + (axis == 2 ? 1 : 0);
                    if ((Content(data, read0, samples, ex, ey, ez) > ISO_LEVEL) == here)
                        continue;

                    // The two axes perpendicular to the edge; cells at offsets 0/-1 along each.
                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;
                    int c00 = CellAt(cellVertex, cellMin, cells, x, y, z, u, v, -1, -1);
                    int c10 = CellAt(cellVertex, cellMin, cells, x, y, z, u, v, 0, -1);
                    int c11 = CellAt(cellVertex, cellMin, cells, x, y, z, u, v, 0, 0);
                    int c01 = CellAt(cellVertex, cellMin, cells, x, y, z, u, v, -1, 0);
                    if (c00 < 0 || c10 < 0 || c11 < 0 || c01 < 0)
                        continue;
                    // The rasterizer draws both windings; only the split matters.
                    indices.Add(c00);
                    indices.Add(c10);
                    indices.Add(c11);
                    indices.Add(c00);
                    indices.Add(c11);
                    indices.Add(c01);
                }
            }

            return new VoxelMesh
            {
                Level = level,
                Positions = positions.ToArray(),
                Normals = normals.ToArray(),
                Materials = materials.ToArray(),
                Indices = indices.ToArray()
            };
        }

        static int CellAt(int[] cellVertex, Vector3I cellMin, Vector3I cells, int x, int y, int z, int u, int v,
            int du, int dv)
        {
            int px = x, py = y, pz = z;
            if (u == 0) px += du; else if (u == 1) py += du; else pz += du;
            if (v == 0) px += dv; else if (v == 1) py += dv; else pz += dv;
            px -= cellMin.X;
            py -= cellMin.Y;
            pz -= cellMin.Z;
            if (px < 0 || py < 0 || pz < 0 || px >= cells.X || py >= cells.Y || pz >= cells.Z)
                return -1;
            return cellVertex[px + cells.X * (py + cells.Y * pz)];
        }

        // A sample by its storage-wide lod coordinates; outside the storage it is empty.
        static float Content(MyStorageData data, Vector3I read0, Vector3I samples, int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= samples.X || y >= samples.Y || z >= samples.Z)
                return 0f;
            return data.Content(x - read0.X, y - read0.Y, z - read0.Z);
        }

        static byte Material(MyStorageData data, Vector3I read0, Vector3I read1, int x, int y, int z)
        {
            x = Math.Max(read0.X, Math.Min(read1.X, x)) - read0.X;
            y = Math.Max(read0.Y, Math.Min(read1.Y, y)) - read0.Y;
            z = Math.Max(read0.Z, Math.Min(read1.Z, z)) - read0.Z;
            var point = new Vector3I(x, y, z);
            return data.Material(ref point);
        }

        static readonly Vector3 NeutralColor = new Vector3(0.42f, 0.40f, 0.38f);

        public void Build()
        {
            _triangles.Clear();
            for (int i = 0; i < _instances.Count; i++)
            {
                Instance instance = _instances[i];
                VoxelMesh mesh = instance.Mesh;
                int count = mesh.Positions.Length;
                if (_transformed.Length < count)
                {
                    _transformed = new Vector3D[count];
                    _lit = new Vector3[count];
                    _normals = new Vector3[count];
                }
                for (int v = 0; v < count; v++)
                {
                    _transformed[v] = Vector3D.Transform((Vector3D)mesh.Positions[v], instance.ToScene);
                    Vector3 normal = (Vector3)Vector3D.TransformNormal((Vector3D)mesh.Normals[v], instance.ToScene);
                    normal.Normalize();
                    _normals[v] = normal;
                    // Ambient plus a directional term, like the grid blocks but one-sided: voxels are closed.
                    float shade = 0.42f + 0.62f * Math.Max(0f, Vector3.Dot(normal, _light));
                    _lit[v] = (_colors == null ? NeutralColor : _colors.Get(mesh.Materials[v])) * shade;
                }

                int[] indices = mesh.Indices;
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    if (IsBackFace(indices[t], indices[t + 1], indices[t + 2]))
                        continue;
                    SceneVertex a;
                    SceneVertex b;
                    SceneVertex c;
                    _camera.ToCameraSpace(ref _transformed[indices[t]], out a);
                    _camera.ToCameraSpace(ref _transformed[indices[t + 1]], out b);
                    _camera.ToCameraSpace(ref _transformed[indices[t + 2]], out c);
                    a.Attribute = _lit[indices[t]];
                    b.Attribute = _lit[indices[t + 1]];
                    c.Attribute = _lit[indices[t + 2]];
                    _triangles.Add(ref _camera, ref a, ref b, ref c);
                }
            }
            _triangles.Bin(_camera.Height);
        }

        // Voxel maps are closed surfaces: a triangle facing away from the camera is always behind the front
        // one, so it is never rasterized. The face normal is oriented by the vertex normals (outward), so the
        // test does not depend on the mesher's index winding.
        bool IsBackFace(int i0, int i1, int i2)
        {
            Vector3D p0 = _transformed[i0];
            Vector3D face = Vector3D.Cross(_transformed[i1] - p0, _transformed[i2] - p0);
            double outward = Vector3D.Dot(face, (Vector3D)(_normals[i0] + _normals[i1] + _normals[i2]));
            if (outward == 0d)
                return false;
            double toward = Vector3D.Dot(face, p0 - _camera.Position);
            return outward > 0d ? toward > 0d : toward < 0d;
        }

        public void Draw(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow)
        {
            if (_triangles.Count == 0 || depthBuffer == null)
                return;
            SceneRasterizer.DrawGouraud(canvas, depthBuffer, _triangles, firstRow, endRow);
        }
    }
}
