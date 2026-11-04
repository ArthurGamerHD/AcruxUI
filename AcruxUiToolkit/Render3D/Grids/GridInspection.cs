using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Acrux.Render3D
{
    // The damaged blocks of a grid and the missing blocks of its reference. Either side is a live grid, copied
    // into a light snapshot a few thousand blocks per tick, or a snapshot kept in memory. The comparison runs on a
    // background worker against those copies, with no engine calls, and its result is published whole. Published
    // collections are never changed again: the paint threads read colors derived from them.
    internal sealed class GridInspection
    {
        const double RESCAN_SECONDS = 1d;
        // Matching a snapshot whose pivot is gone: candidate placements from rare blocks, scored on a sample.
        const int ANCHORS = 12;
        const int PLACEMENTS_PER_ANCHOR = 48;
        const int SAMPLE_BLOCKS = 256;
        const int MINIMUM_MATCHES = 4;

        /// <summary>Ints per entry of <see cref="Cells"/>.</summary>
        public const int CELL_STRIDE = 8;
        public const int CELL_MISSING = 1;
        public const int CELL_DAMAGED = 2;

        sealed class Pass
        {
            public GridSnapshotCapture GridCapture;
            public GridSnapshotCapture ReferenceCapture;
            public GridSnapshot Grid;
            public GridSnapshot Reference;
            public bool GridIsMemory;
            public bool HasReference;
            public bool HasPlacement;
            public MatrixD ReferenceToGrid = MatrixD.Identity;
            public Dictionary<Vector3I, float> Damage;
            public List<Vector3I> Missing;
            public List<Vector3I>[] Categories;
            public string[] CommonBlocks;
            public Dictionary<long, Dictionary<Vector3I, float>> PartDamage;
            public Dictionary<long, List<Vector3I>> PartMissing;
            public Dictionary<long, List<Vector3I>[]> PartCategories;
            public int[] Cells;
            // Live grids only: integrity by position per piece (zero: the grid, else a subgrid's id), the last pass's
            // and this one's, and the blocks that lost integrity or went in between (piece id, x, y, z each).
            public Dictionary<long, Dictionary<Vector3I, float>> PreviousIntegrity;
            public Dictionary<long, Dictionary<Vector3I, float>> Integrity;
            public long[] Hits;
            public bool Mismatch;
            public Exception Failure;
            // Set last by the worker (volatile: its writes above are visible once this reads true).
            public volatile bool Done;
        }

        long _gridId;
        long _gridMemoryKey;
        long _referenceId;
        long _referenceMemoryKey;
        Pass _pass;
        bool _computing;
        Dictionary<long, Dictionary<Vector3I, float>> _integrity;
        DateTime _passEnded;

        public Dictionary<Vector3I, float> Damage { get; private set; }
        public List<Vector3I> Missing { get; private set; }
        // Block positions per GridBlockCategory, indexed by its value; None is left empty.
        public List<Vector3I>[] Categories { get; private set; }
        // The definition id the grid has most of, per category.
        public string[] CommonBlocks { get; private set; }
        // The same per subgrid, by the subgrid's entity id (as captured, for a snapshot's part).
        public Dictionary<long, Dictionary<Vector3I, float>> PartDamage { get; private set; }
        // Per subgrid of the reference snapshot, by its entity id: its blocks missing, in its own cells.
        public Dictionary<long, List<Vector3I>> PartMissing { get; private set; }
        public Dictionary<long, List<Vector3I>[]> PartCategories { get; private set; }
        public int DamagedCount { get; private set; }
        // The blocks hit between the last two passes of a live grid: piece id, x, y, z each; empty for a snapshot.
        public long[] Hits { get; private set; }
        public int MissingCount { get; private set; }
        // The reference's blocks (the grid's without one) in the drawn grid's own axes, CELL_STRIDE ints each:
        // min x, y, z, max x, y, z, CELL_ flags, category.
        public int[] Cells { get; private set; }
        // Reference cells to grid cells, each in its own snapshot's cells.
        public MatrixD ReferenceToGrid { get; private set; }
        public bool HasReference { get; private set; }
        // A reference snapshot that could not be placed on the grid (another grid, or too little of it left).
        public bool ReferenceMismatch { get; private set; }
        // The drawn grid is the memory snapshot.
        public bool GridIsMemory { get; private set; }
        public int Version { get; private set; }

        public bool IsScanning
        {
            get { return _pass != null && Version == 0; }
        }

        public void Reset()
        {
            _gridId = 0;
            _gridMemoryKey = 0;
            _referenceId = 0;
            _referenceMemoryKey = 0;
            // A pass still computing finishes on its own; nothing reads it again.
            _pass = null;
            _computing = false;
            _integrity = null;
            Hits = null;
            Damage = null;
            Missing = null;
            Categories = null;
            CommonBlocks = null;
            PartDamage = null;
            PartMissing = null;
            PartCategories = null;
            DamagedCount = 0;
            MissingCount = 0;
            Cells = null;
            HasReference = false;
            ReferenceMismatch = false;
            GridIsMemory = false;
            ReferenceToGrid = MatrixD.Identity;
            Version = 0;
        }

        // Game thread. The live grid wins over the memory snapshot, a live reference over a reference snapshot.
        // True when a pass finished and published new results.
        public bool Update(IMyCubeGrid grid, GridSnapshot gridMemory, IMyCubeGrid reference, GridSnapshot referenceMemory)
        {
            if (grid != null)
                gridMemory = null;
            if (reference != null || (grid == null && gridMemory == null))
                referenceMemory = null;
            long gridId = grid == null ? 0 : grid.EntityId;
            long gridMemoryKey = gridMemory == null ? 0 : gridMemory.CacheKey;
            long referenceId = reference == null ? 0 : reference.EntityId;
            long referenceMemoryKey = referenceMemory == null ? 0 : referenceMemory.CacheKey;
            if (gridId != _gridId || gridMemoryKey != _gridMemoryKey || referenceId != _referenceId ||
                referenceMemoryKey != _referenceMemoryKey)
            {
                Reset();
                _gridId = gridId;
                _gridMemoryKey = gridMemoryKey;
                _referenceId = referenceId;
                _referenceMemoryKey = referenceMemoryKey;
            }
            if (grid == null && gridMemory == null)
                return false;

            DateTime now = DateTime.UtcNow;
            if (_pass == null)
            {
                // Snapshots never change: with no live grid on either side one pass is all it needs.
                bool live = grid != null || reference != null;
                if (Version != 0 && (!live || (now - _passEnded).TotalSeconds < RESCAN_SECONDS))
                    return false;
                _pass = new Pass
                {
                    HasReference = reference != null || referenceMemory != null,
                    PreviousIntegrity = _integrity
                };
                if (grid != null)
                    _pass.GridCapture = new GridSnapshotCapture(grid, 0, false);
                else
                {
                    _pass.Grid = gridMemory;
                    _pass.GridIsMemory = true;
                }
                if (reference != null)
                    _pass.ReferenceCapture = new GridSnapshotCapture(reference, 0, false);
                else
                    _pass.Reference = referenceMemory;
            }
            if (_computing)
                return _pass.Done && Publish(now);

            Pass pass = _pass;
            if (pass.GridCapture != null && !pass.GridCapture.IsDone)
            {
                pass.GridCapture.Step();
                return false;
            }
            if (pass.ReferenceCapture != null && !pass.ReferenceCapture.IsDone)
            {
                pass.ReferenceCapture.Step();
                return false;
            }
            if (pass.GridCapture != null)
                pass.Grid = pass.GridCapture.Result;
            if (pass.ReferenceCapture != null)
                pass.Reference = pass.ReferenceCapture.Result;
            if (pass.Grid == null)
            {
                _pass = null;
                _passEnded = now;
                return false;
            }
            if (pass.Reference == null)
                pass.HasReference = false;
            if (pass.HasReference)
                PlaceReference(pass, grid, reference, referenceMemory);
            StartCompute(pass);
            return false;
        }

        // Game thread: where the reference lies on the grid when the game can tell; otherwise the worker matches it.
        static void PlaceReference(Pass pass, IMyCubeGrid grid, IMyCubeGrid reference, GridSnapshot referenceMemory)
        {
            if (reference != null && grid != null)
            {
                // Reference cells to grid cells: GridIntegerToWorld then WorldToGridInteger, as one matrix.
                pass.ReferenceToGrid = MatrixD.CreateScale(reference.GridSize) * reference.WorldMatrix *
                                       MatrixD.Invert(grid.WorldMatrix) * MatrixD.CreateScale(1d / grid.GridSize);
                pass.HasPlacement = true;
                return;
            }
            if (referenceMemory == null)
                return;
            if (grid != null)
            {
                if (referenceMemory.PivotEntityId == 0 || MyAPIGateway.Entities == null)
                    return;
                var pivot = MyAPIGateway.Entities.GetEntityById(referenceMemory.PivotEntityId) as IMyCubeBlock;
                if (pivot == null || pivot.Closed || pivot.CubeGrid != grid)
                    return;
                pass.ReferenceToGrid = GridSnapshot.Pose(pivot.Position, pivot.Orientation.Forward,
                    pivot.Orientation.Up, 1d);
                pass.HasPlacement = true;
                return;
            }
            GridSnapshot memory = pass.Grid;
            if (ReferenceEquals(memory, referenceMemory) ||
                (memory.PivotEntityId != 0 && memory.PivotEntityId == referenceMemory.PivotEntityId))
                pass.HasPlacement = true;
        }

        void StartCompute(Pass pass)
        {
            _computing = true;
            if (MyAPIGateway.Parallel == null)
            {
                Compute(pass);
                return;
            }
            try
            {
                MyAPIGateway.Parallel.StartBackground(() => Compute(pass));
            }
            catch (Exception)
            {
                Compute(pass);
            }
        }

        static void Compute(Pass pass)
        {
            try
            {
                GridSnapshot grid = pass.Grid;
                GridSnapshot.Block[] blocks = grid.Blocks;
                var damage = new Dictionary<Vector3I, float>();
                // Every cell a block fills, to its block's index.
                var occupied = new Dictionary<Vector3I, int>();
                var byPosition = new Dictionary<Vector3I, int>(blocks.Length);
                var categories = new List<Vector3I>[(int)GridBlockCategory.Conveyors + 1];
                var counts = new Dictionary<MyDefinitionId, int>[categories.Length];
                for (int c = 0; c < categories.Length; c++)
                {
                    categories[c] = new List<Vector3I>();
                    counts[c] = new Dictionary<MyDefinitionId, int>();
                }
                for (int i = 0; i < blocks.Length; i++)
                {
                    GridSnapshot.Block block = blocks[i];
                    GridSnapshot.Definition definition = grid.Definitions[block.Definition];
                    byPosition[block.Position] = i;
                    if (definition.Category != GridBlockCategory.None)
                    {
                        categories[(int)definition.Category].Add(block.Position);
                        Dictionary<MyDefinitionId, int> count = counts[(int)definition.Category];
                        int seen;
                        count.TryGetValue(definition.Id, out seen);
                        count[definition.Id] = seen + 1;
                    }
                    if (definition.MaxIntegrity > 0f && block.Integrity < definition.MaxIntegrity * 0.999f)
                        damage[block.Position] = MathHelper.Clamp(1f - block.Integrity / definition.MaxIntegrity, 0f, 1f);
                    if (!pass.HasReference)
                        continue;
                    for (int x = block.Min.X; x <= block.Max.X; x++)
                        for (int y = block.Min.Y; y <= block.Max.Y; y++)
                            for (int z = block.Min.Z; z <= block.Max.Z; z++)
                                occupied[new Vector3I(x, y, z)] = i;
                }

                if (pass.HasReference && !pass.HasPlacement)
                {
                    MatrixD placement;
                    if (Match(pass.Reference, grid, byPosition, out placement))
                    {
                        pass.ReferenceToGrid = placement;
                        pass.HasPlacement = true;
                    }
                    else
                    {
                        pass.Mismatch = true;
                        pass.HasReference = false;
                    }
                }

                MatrixD toDisplay = pass.GridIsMemory ? grid.PivotToCaptured : MatrixD.Identity;
                var missing = new List<Vector3I>();
                var cells = new List<int>();
                if (pass.HasReference)
                {
                    GridSnapshot reference = pass.Reference;
                    MatrixD toGrid = pass.ReferenceToGrid;
                    MatrixD referenceToDisplay = toGrid * toDisplay;
                    for (int i = 0; i < reference.Blocks.Length; i++)
                    {
                        GridSnapshot.Block block = reference.Blocks[i];
                        GridSnapshot.Definition wanted = reference.Definitions[block.Definition];
                        Vector3I cell = GridSnapshotCapture.Cell(block.Position, ref toGrid);
                        int index;
                        int flags = 0;
                        GridBlockCategory category = wanted.Category;
                        if (occupied.TryGetValue(cell, out index) && grid.Definitions[blocks[index].Definition].Id == wanted.Id)
                        {
                            GridSnapshot.Definition real = grid.Definitions[blocks[index].Definition];
                            if (Ratio(blocks[index].Integrity, real.MaxIntegrity) <
                                Ratio(block.Integrity, wanted.MaxIntegrity) - 0.001f)
                                flags |= CELL_DAMAGED;
                            if (category == GridBlockCategory.None)
                                category = real.Category;
                        }
                        else
                        {
                            missing.Add(block.Position);
                            flags |= CELL_MISSING;
                        }
                        AddCell(cells, block, ref referenceToDisplay, flags, category);
                    }
                }
                else
                {
                    for (int i = 0; i < blocks.Length; i++)
                    {
                        GridSnapshot.Definition definition = grid.Definitions[blocks[i].Definition];
                        int flags = damage.ContainsKey(blocks[i].Position) ? CELL_DAMAGED : 0;
                        AddCell(cells, blocks[i], ref toDisplay, flags, definition.Category);
                    }
                }

                ComputeParts(pass, counts, cells, ref toDisplay);
                if (!pass.GridIsMemory)
                    ComputeHits(pass);

                pass.Damage = damage;
                pass.Missing = missing;
                pass.Categories = categories;
                pass.Cells = cells.ToArray();
                var common = new string[categories.Length];
                for (int c = 0; c < counts.Length; c++)
                {
                    int best = 0;
                    foreach (KeyValuePair<MyDefinitionId, int> pair in counts[c])
                    {
                        if (pair.Value <= best)
                            continue;
                        best = pair.Value;
                        common[c] = pair.Key.ToString();
                    }
                }
                pass.CommonBlocks = common;
            }
            catch (Exception exception)
            {
                // Caught on the worker: the engine may treat uncaught background exceptions as fatal.
                pass.Failure = exception;
            }
            pass.Done = true;
        }

        // Subgrids: each of the grid's own is scanned like the grid; each of a reference snapshot's is compared with the
        // grid's subgrid of the same entity id (all of it missing when there is none). A live reference (a projection)
        // has no subgrids to compare.
        static void ComputeParts(Pass pass, Dictionary<MyDefinitionId, int>[] counts, List<int> cells, ref MatrixD toDisplay)
        {
            GridSnapshot grid = pass.Grid;
            double size = grid.GridSize;
            MatrixD displayMetres = Metres(toDisplay, size);
            pass.PartDamage = new Dictionary<long, Dictionary<Vector3I, float>>();
            pass.PartMissing = new Dictionary<long, List<Vector3I>>();
            pass.PartCategories = new Dictionary<long, List<Vector3I>[]>();
            var byId = new Dictionary<long, GridSnapshot>();
            bool compare = pass.HasReference && pass.ReferenceCapture == null;
            for (int p = 0; p < grid.Parts.Length; p++)
            {
                GridSnapshot part = grid.Parts[p];
                byId[part.GridEntityId] = part;
                var damage = new Dictionary<Vector3I, float>();
                var categories = new List<Vector3I>[counts.Length];
                for (int c = 0; c < categories.Length; c++)
                    categories[c] = new List<Vector3I>();
                MatrixD toCells = MatrixD.CreateScale(part.GridSize) * (MatrixD)part.ToParent * displayMetres *
                                  MatrixD.CreateScale(1d / size);
                for (int i = 0; i < part.Blocks.Length; i++)
                {
                    GridSnapshot.Block block = part.Blocks[i];
                    GridSnapshot.Definition definition = part.Definitions[block.Definition];
                    if (definition.Category != GridBlockCategory.None)
                    {
                        categories[(int)definition.Category].Add(block.Position);
                        Dictionary<MyDefinitionId, int> count = counts[(int)definition.Category];
                        int seen;
                        count.TryGetValue(definition.Id, out seen);
                        count[definition.Id] = seen + 1;
                    }
                    bool damaged = definition.MaxIntegrity > 0f && block.Integrity < definition.MaxIntegrity * 0.999f;
                    if (damaged)
                        damage[block.Position] = MathHelper.Clamp(1f - block.Integrity / definition.MaxIntegrity, 0f, 1f);
                    if (!compare)
                        AddCell(cells, block, ref toCells, damaged ? CELL_DAMAGED : 0, definition.Category);
                }
                pass.PartDamage[part.GridEntityId] = damage;
                pass.PartCategories[part.GridEntityId] = categories;
            }
            if (!compare)
                return;

            GridSnapshot reference = pass.Reference;
            MatrixD referenceMetres = Metres(pass.ReferenceToGrid, size) * displayMetres;
            for (int p = 0; p < reference.Parts.Length; p++)
            {
                GridSnapshot wantedPart = reference.Parts[p];
                GridSnapshot realPart;
                byId.TryGetValue(wantedPart.GridEntityId, out realPart);
                var occupied = new Dictionary<Vector3I, int>();
                if (realPart != null)
                {
                    for (int i = 0; i < realPart.Blocks.Length; i++)
                    {
                        GridSnapshot.Block block = realPart.Blocks[i];
                        for (int x = block.Min.X; x <= block.Max.X; x++)
                            for (int y = block.Min.Y; y <= block.Max.Y; y++)
                                for (int z = block.Min.Z; z <= block.Max.Z; z++)
                                    occupied[new Vector3I(x, y, z)] = i;
                    }
                }
                var missing = new List<Vector3I>();
                MatrixD toCells = MatrixD.CreateScale(wantedPart.GridSize) * (MatrixD)wantedPart.ToParent * referenceMetres *
                                  MatrixD.CreateScale(1d / size);
                for (int i = 0; i < wantedPart.Blocks.Length; i++)
                {
                    GridSnapshot.Block block = wantedPart.Blocks[i];
                    GridSnapshot.Definition wanted = wantedPart.Definitions[block.Definition];
                    int index;
                    int flags = 0;
                    if (realPart != null && occupied.TryGetValue(block.Position, out index) &&
                        realPart.Definitions[realPart.Blocks[index].Definition].Id == wanted.Id)
                    {
                        GridSnapshot.Block real = realPart.Blocks[index];
                        if (Ratio(real.Integrity, realPart.Definitions[real.Definition].MaxIntegrity) <
                            Ratio(block.Integrity, wanted.MaxIntegrity) - 0.001f)
                            flags |= CELL_DAMAGED;
                    }
                    else
                    {
                        missing.Add(block.Position);
                        flags |= CELL_MISSING;
                    }
                    AddCell(cells, block, ref toCells, flags, wanted.Category);
                }
                pass.PartMissing[wantedPart.GridEntityId] = missing;
            }
        }

        static void ComputeHits(Pass pass)
        {
            var integrity = new Dictionary<long, Dictionary<Vector3I, float>>();
            integrity[0] = IntegrityOf(pass.Grid);
            for (int p = 0; p < pass.Grid.Parts.Length; p++)
                integrity[pass.Grid.Parts[p].GridEntityId] = IntegrityOf(pass.Grid.Parts[p]);
            pass.Integrity = integrity;
            var hits = new List<long>();
            if (pass.PreviousIntegrity != null)
            {
                foreach (KeyValuePair<long, Dictionary<Vector3I, float>> piece in pass.PreviousIntegrity)
                {
                    Dictionary<Vector3I, float> now;
                    integrity.TryGetValue(piece.Key, out now);
                    foreach (KeyValuePair<Vector3I, float> block in piece.Value)
                    {
                        float current;
                        if (now != null && now.TryGetValue(block.Key, out current) && current >= block.Value - 0.0001f)
                            continue;
                        hits.Add(piece.Key);
                        hits.Add(block.Key.X);
                        hits.Add(block.Key.Y);
                        hits.Add(block.Key.Z);
                    }
                }
            }
            pass.Hits = hits.ToArray();
        }

        static Dictionary<Vector3I, float> IntegrityOf(GridSnapshot grid)
        {
            var integrity = new Dictionary<Vector3I, float>(grid.Blocks.Length);
            for (int i = 0; i < grid.Blocks.Length; i++)
            {
                GridSnapshot.Block block = grid.Blocks[i];
                float maximum = grid.Definitions[block.Definition].MaxIntegrity;
                integrity[block.Position] = maximum > 0f ? block.Integrity / maximum : 1f;
            }
            return integrity;
        }

        // A matrix between cells as one between metres.
        internal static MatrixD Metres(MatrixD cells, double gridSize)
        {
            cells.Translation = cells.Translation * gridSize;
            return cells;
        }

        static float Ratio(float integrity, float maximum)
        {
            return maximum > 0f ? integrity / maximum : 1f;
        }

        static void AddCell(List<int> cells, GridSnapshot.Block block, ref MatrixD matrix, int flags,
            GridBlockCategory category)
        {
            Vector3I a = GridSnapshotCapture.Cell(block.Min, ref matrix);
            Vector3I b = GridSnapshotCapture.Cell(block.Max, ref matrix);
            Vector3I min = Vector3I.Min(a, b);
            Vector3I max = Vector3I.Max(a, b);
            cells.Add(min.X);
            cells.Add(min.Y);
            cells.Add(min.Z);
            cells.Add(max.X);
            cells.Add(max.Y);
            cells.Add(max.Z);
            cells.Add(flags);
            cells.Add((int)category);
        }

        // Where the snapshot lies on the grid when its pivot is gone: each placement that puts one of its rarer
        // blocks onto a grid block of the same kind and orientation is scored by how many of a sample of its blocks
        // then land on a block of the same definition. Survives a new grid origin, a split and a merge.
        static bool Match(GridSnapshot from, GridSnapshot to, Dictionary<Vector3I, int> toByPosition, out MatrixD placement)
        {
            placement = MatrixD.Identity;
            if (from.Blocks.Length == 0 || to.Blocks.Length == 0)
                return false;
            var toByDefinition = new Dictionary<MyDefinitionId, List<int>>();
            for (int i = 0; i < to.Blocks.Length; i++)
            {
                MyDefinitionId id = to.Definitions[to.Blocks[i].Definition].Id;
                List<int> list;
                if (!toByDefinition.TryGetValue(id, out list))
                {
                    list = new List<int>();
                    toByDefinition[id] = list;
                }
                list.Add(i);
            }
            var fromCounts = new int[from.Definitions.Length];
            for (int i = 0; i < from.Blocks.Length; i++)
                fromCounts[from.Blocks[i].Definition]++;

            var anchors = new List<int>();
            for (int i = 0; i < from.Blocks.Length; i++)
            {
                if (toByDefinition.ContainsKey(from.Definitions[from.Blocks[i].Definition].Id))
                    anchors.Add(i);
            }
            if (anchors.Count == 0)
                return false;
            anchors.Sort((a, b) => fromCounts[from.Blocks[a].Definition].CompareTo(fromCounts[from.Blocks[b].Definition]));
            if (anchors.Count > ANCHORS)
                anchors.RemoveRange(ANCHORS, anchors.Count - ANCHORS);

            int stride = Math.Max(1, from.Blocks.Length / SAMPLE_BLOCKS);
            var sample = new List<int>();
            for (int i = 0; i < from.Blocks.Length; i += stride)
                sample.Add(i);

            int best = 0;
            for (int a = 0; a < anchors.Count; a++)
            {
                GridSnapshot.Block anchor = from.Blocks[anchors[a]];
                MatrixD anchorRotation = GridSnapshot.Pose(Vector3I.Zero, anchor.Forward, anchor.Up, 1d);
                MatrixD inverse = MatrixD.Transpose(anchorRotation);
                List<int> targets = toByDefinition[from.Definitions[anchor.Definition].Id];
                int tries = Math.Min(targets.Count, PLACEMENTS_PER_ANCHOR);
                for (int t = 0; t < tries; t++)
                {
                    GridSnapshot.Block target = to.Blocks[targets[t]];
                    MatrixD rotation = inverse * GridSnapshot.Pose(Vector3I.Zero, target.Forward, target.Up, 1d);
                    rotation.Translation = Vector3D.Zero;
                    Vector3I moved = GridSnapshotCapture.Cell(anchor.Position, ref rotation);
                    rotation.Translation = new Vector3D(target.Position - moved);
                    int score = 0;
                    for (int s = 0; s < sample.Count; s++)
                    {
                        GridSnapshot.Block block = from.Blocks[sample[s]];
                        int index;
                        if (toByPosition.TryGetValue(GridSnapshotCapture.Cell(block.Position, ref rotation), out index) &&
                            to.Definitions[to.Blocks[index].Definition].Id == from.Definitions[block.Definition].Id)
                            score++;
                    }
                    if (score <= best)
                        continue;
                    best = score;
                    placement = rotation;
                }
            }
            return best >= Math.Max(MINIMUM_MATCHES, Math.Min(sample.Count, to.Blocks.Length) / 5);
        }

        bool Publish(DateTime now)
        {
            Pass pass = _pass;
            _pass = null;
            _computing = false;
            _passEnded = now;
            if (pass.Failure != null)
                return false;
            Damage = pass.Damage;
            Missing = pass.Missing;
            Categories = pass.Categories;
            CommonBlocks = pass.CommonBlocks;
            PartDamage = pass.PartDamage;
            PartMissing = pass.PartMissing;
            PartCategories = pass.PartCategories;
            DamagedCount = pass.Damage.Count;
            foreach (Dictionary<Vector3I, float> damage in pass.PartDamage.Values)
                DamagedCount += damage.Count;
            MissingCount = pass.Missing.Count;
            foreach (List<Vector3I> missing in pass.PartMissing.Values)
                MissingCount += missing.Count;
            Cells = pass.Cells;
            _integrity = pass.Integrity;
            Hits = pass.Hits;
            HasReference = pass.HasReference;
            ReferenceMismatch = pass.Mismatch;
            ReferenceToGrid = pass.ReferenceToGrid;
            GridIsMemory = pass.GridIsMemory;
            Version++;
            return true;
        }
    }
}
