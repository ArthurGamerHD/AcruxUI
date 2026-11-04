using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// Takes a <see cref="GridSnapshot"/> of a grid over a few ticks: <see cref="Step"/> copies a few thousand blocks'
    /// plain values per call on the game thread, and a background worker turns them into the snapshot.
    /// </summary>
    public sealed class GridSnapshotCapture
    {
        const int BLOCKS_PER_STEP = 4096;
        const int MAXIMUM_SUBPART_DEPTH = 3;

        struct RawBlock
        {
            public Vector3I Position;
            public Vector3I Min;
            public Vector3I Max;
            public Base6Directions.Direction Forward;
            public Base6Directions.Direction Up;
            public int Definition;
            public float Integrity;
            public int Color;
            public int Local;
        }

        readonly bool _full;
        readonly byte[] _data;
        // A subgrid of another capture: no pivot and no parts of its own.
        readonly bool _part;
        readonly List<GridSnapshotCapture> _parts = new List<GridSnapshotCapture>();
        // Each part's metres to the main grid's metres, when captured.
        readonly List<MatrixD> _partToMain = new List<MatrixD>();
        readonly List<IMyCubeGrid> _group = new List<IMyCubeGrid>();
        int _partIndex;
        readonly long _pivotRequest;
        readonly List<IMySlimBlock> _slims = new List<IMySlimBlock>();
        readonly List<RawBlock> _blocks = new List<RawBlock>();
        readonly List<GridSnapshot.Definition> _definitions = new List<GridSnapshot.Definition>();
        readonly Dictionary<MyDefinitionId, int> _definitionIndex = new Dictionary<MyDefinitionId, int>();
        readonly List<string> _models = new List<string>();
        readonly Dictionary<string, int> _modelIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly List<Vector3> _colors = new List<Vector3>();
        readonly Dictionary<Vector3, int> _colorIndex = new Dictionary<Vector3, int>();
        readonly List<GridSnapshot.Local> _locals = new List<GridSnapshot.Local>();
        readonly List<GridSnapshot.Subpart> _subparts = new List<GridSnapshot.Subpart>();
        IMyCubeGrid _grid;
        MatrixD _worldToGrid;
        Vector3D _center;
        int _index;
        int _firstSubpart;
        bool _started;
        IMySlimBlock _pivot;
        double _pivotDistance = double.MaxValue;
        GridSnapshot _snapshot;
        volatile bool _done;

        /// <summary>
        /// Starts a snapshot of the grid and the grids mechanically attached to it (rotors, pistons, hinges), its
        /// blocks kept relative to the block <paramref name="pivotBlockId"/> (an LCD's own block). Zero, or a block not
        /// on the grid, picks the functional block nearest the grid's centre.
        /// </summary>
        public GridSnapshotCapture(IMyCubeGrid grid, long pivotBlockId) : this(grid, pivotBlockId, true)
        {
        }

        /// <summary>
        /// Reads a snapshot back from <see cref="GridSnapshot.ToBytes"/> on a background worker instead: the first
        /// <see cref="Step"/> starts it.
        /// </summary>
        public GridSnapshotCapture(byte[] data)
        {
            _data = data;
            _full = true;
        }

        // Light: no models, paint or pivot, for comparing blocks every second.
        internal GridSnapshotCapture(IMyCubeGrid grid, long pivotBlockId, bool full, bool part = false)
        {
            _grid = grid;
            _pivotRequest = pivotBlockId;
            _full = full;
            _part = part;
        }

        /// <summary>The capture finished, with <see cref="Result"/> or <see cref="Error"/>.</summary>
        public bool IsDone
        {
            get { return _done; }
        }

        /// <summary>The snapshot once done; null while capturing or when it failed.</summary>
        public GridSnapshot Result
        {
            get { return _done ? _snapshot : null; }
        }

        /// <summary>Why the capture failed (the grid closed), or null.</summary>
        public string Error { get; private set; }

        /// <summary>Game thread, every tick until it returns true: copies the next blocks.</summary>
        public bool Step()
        {
            if (_done || _started)
                return _done;
            if (_data != null)
            {
                _started = true;
                Run(Decode);
                return _done;
            }
            if (_grid == null || _grid.MarkedForClose || _grid.Closed)
            {
                Fail("The grid is gone");
                return true;
            }
            if (_slims.Count == 0 && _index == 0)
            {
                _grid.GetBlocks(_slims);
                _worldToGrid = MatrixD.Invert(_grid.WorldMatrix);
                if (!_part)
                    StartParts();
                _center = (new Vector3D(_grid.Min) + new Vector3D(_grid.Max)) * 0.5d;
                if (_slims.Count == 0)
                {
                    Fail("The grid has no blocks");
                    return true;
                }
            }
            int end = Math.Min(_slims.Count, _index + BLOCKS_PER_STEP);
            for (; _index < end; _index++)
            {
                IMySlimBlock slim = _slims[_index];
                if (slim != null && !slim.IsDestroyed)
                    Copy(slim);
            }
            if (_index < _slims.Count)
                return false;
            // The parts copy after the grid, one step each per call; a part that failed (closed meanwhile) is left out.
            while (_partIndex < _parts.Count)
            {
                if (!_parts[_partIndex].Step())
                    return false;
                _partIndex++;
            }
            for (int i = 0; i < _parts.Count; i++)
            {
                if (!_parts[i].IsDone)
                    return false;
            }
            Finish();
            return _done;
        }

        void StartParts()
        {
            if (MyAPIGateway.GridGroups == null)
                return;
            _group.Clear();
            MyAPIGateway.GridGroups.GetGroup(_grid, GridLinkTypeEnum.Mechanical, _group);
            for (int i = 0; i < _group.Count; i++)
            {
                IMyCubeGrid grid = _group[i];
                if (grid == null || grid == _grid || grid.MarkedForClose)
                    continue;
                _parts.Add(new GridSnapshotCapture(grid, 0, _full, true));
                _partToMain.Add(grid.WorldMatrix * _worldToGrid);
            }
            _group.Clear();
        }

        void Fail(string error)
        {
            Error = error;
            _slims.Clear();
            _done = true;
        }

        void Copy(IMySlimBlock slim)
        {
            IMyCubeBlock fat = slim.FatBlock;
            var block = new RawBlock
            {
                Position = slim.Position,
                Min = slim.Min,
                Max = slim.Max,
                Forward = slim.Orientation.Forward,
                Up = slim.Orientation.Up,
                Definition = DefinitionOf(slim, fat),
                Integrity = slim.Integrity,
                Local = -1
            };
            if (_full)
            {
                block.Color = ColorOf(slim.ColorMaskHSV);
                IMyModel model = fat == null ? null : fat.Model;
                if (model != null && !string.IsNullOrEmpty(model.AssetName))
                {
                    var local = new GridSnapshot.Local
                    {
                        Model = ModelOf(model.AssetName),
                        Matrix = (Matrix)(fat.WorldMatrix * _worldToGrid),
                        FirstSubpart = _subparts.Count
                    };
                    _firstSubpart = local.FirstSubpart;
                    AddSubparts(fat as MyEntity, 0);
                    local.SubpartCount = _subparts.Count - local.FirstSubpart;
                    block.Local = _locals.Count;
                    _locals.Add(local);
                }
                if (fat != null && !_part)
                {
                    if (fat.EntityId == _pivotRequest)
                    {
                        _pivot = slim;
                        _pivotDistance = -1d;
                    }
                    else
                    {
                        double distance = Vector3D.DistanceSquared(new Vector3D(slim.Position), _center);
                        if (distance < _pivotDistance)
                        {
                            _pivotDistance = distance;
                            _pivot = slim;
                        }
                    }
                }
            }
            _blocks.Add(block);
        }

        void AddSubparts(MyEntity entity, int depth)
        {
            if (entity == null || entity.Subparts == null || depth >= MAXIMUM_SUBPART_DEPTH)
                return;
            foreach (MyEntitySubpart subpart in entity.Subparts.Values)
            {
                if (subpart == null || subpart.MarkedForClose)
                    continue;
                IMyModel model = ((IMyEntity)subpart).Model;
                if (model != null && !string.IsNullOrEmpty(model.AssetName) && _subparts.Count - _firstSubpart < byte.MaxValue)
                {
                    _subparts.Add(new GridSnapshot.Subpart
                    {
                        Model = ModelOf(model.AssetName),
                        Matrix = (Matrix)(subpart.WorldMatrix * _worldToGrid)
                    });
                }
                AddSubparts(subpart, depth + 1);
            }
        }

        int DefinitionOf(IMySlimBlock slim, IMyCubeBlock fat)
        {
            MyDefinitionId id = slim.BlockDefinition.Id;
            int index;
            if (_definitionIndex.TryGetValue(id, out index))
                return index;
            var definition = slim.BlockDefinition as MyCubeBlockDefinition;
            bool box = definition != null && definition.CubeDefinition != null &&
                       definition.CubeDefinition.CubeTopology == MyCubeTopology.Box;
            byte flags = 0;
            if (fat != null)
                flags |= GridSnapshot.FLAG_ENTITY;
            if (fat == null && box && slim.Min == slim.Max)
                flags |= GridSnapshot.FLAG_FULL_CUBE;
            if ((definition != null && definition.IsAirTight == true) || (fat == null && box))
                flags |= GridSnapshot.FLAG_WALL;
            index = _definitions.Count;
            _definitions.Add(new GridSnapshot.Definition
            {
                Id = id,
                MaxIntegrity = slim.MaxIntegrity,
                Category = Classify(fat),
                Flags = flags
            });
            _definitionIndex[id] = index;
            return index;
        }

        int ModelOf(string asset)
        {
            int index;
            if (_modelIndex.TryGetValue(asset, out index))
                return index;
            index = _models.Count;
            _models.Add(asset);
            _modelIndex[asset] = index;
            return index;
        }

        int ColorOf(Vector3 hsv)
        {
            int index;
            if (_colorIndex.TryGetValue(hsv, out index))
                return index;
            index = _colors.Count;
            _colors.Add(hsv);
            _colorIndex[hsv] = index;
            return index;
        }

        // Interface checks only: cheap enough for the copy on the game thread.
        internal static GridBlockCategory Classify(IMyCubeBlock block)
        {
            if (block == null)
                return GridBlockCategory.None;
            if (block is IMyThrust)
                return GridBlockCategory.Thrusters;
            if (block is IMyPowerProducer)
                return GridBlockCategory.Power;
            if (block is IMyGyro)
                return GridBlockCategory.Gyroscopes;
            if (block is IMyUserControllableGun || block is IMyWarhead)
                return GridBlockCategory.Weapons;
            if (block is IMyGravityGeneratorBase || block is IMyArtificialMassBlock)
                return GridBlockCategory.Gravity;
            if (block is IMyCargoContainer)
                return GridBlockCategory.Cargo;
            // Before production: a sorter is no production block.
            if (block is IMyConveyor || block is IMyConveyorTube || block is IMyConveyorSorter)
                return GridBlockCategory.Conveyors;
            if (block is IMyProductionBlock)
                return GridBlockCategory.Production;
            return GridBlockCategory.None;
        }

        void Finish()
        {
            _started = true;
            _slims.Clear();
            var snapshot = new GridSnapshot
            {
                GridSize = _grid.GridSize,
                GridEntityId = _grid.EntityId,
                GridName = _grid.CustomName,
                CapturedTicks = DateTime.UtcNow.Ticks
            };
            if (_full && _pivot != null)
            {
                snapshot.PivotPosition = _pivot.Position;
                snapshot.PivotForward = _pivot.Orientation.Forward;
                snapshot.PivotUp = _pivot.Orientation.Up;
                snapshot.PivotEntityId = _pivot.FatBlock == null ? 0 : _pivot.FatBlock.EntityId;
            }
            _grid = null;
            _pivot = null;
            Run(() => Build(snapshot));
        }

        static void Run(Action work)
        {
            if (MyAPIGateway.Parallel == null)
            {
                work();
                return;
            }
            try
            {
                MyAPIGateway.Parallel.StartBackground(work);
            }
            catch (Exception)
            {
                work();
            }
        }

        void Decode()
        {
            // FromBytes catches its own failures.
            _snapshot = GridSnapshot.FromBytes(_data);
            if (_snapshot == null)
                Error = "No snapshot in the data";
            _done = true;
        }

        // Worker: the blocks into the pivot's cells.
        void Build(GridSnapshot snapshot)
        {
            try
            {
                MatrixD toPivot = MatrixD.Invert(snapshot.PivotToCaptured);
                MatrixD toPivotMetres = MatrixD.Invert(GridSnapshot.Pose(snapshot.PivotPosition, snapshot.PivotForward,
                    snapshot.PivotUp, snapshot.GridSize));
                bool identity = snapshot.PivotPosition == Vector3I.Zero &&
                                snapshot.PivotForward == Base6Directions.Direction.Forward &&
                                snapshot.PivotUp == Base6Directions.Direction.Up;
                var min = new Vector3I(int.MaxValue);
                var max = new Vector3I(int.MinValue);
                var blocks = new GridSnapshot.Block[_blocks.Count];
                for (int i = 0; i < blocks.Length; i++)
                {
                    RawBlock raw = _blocks[i];
                    min = Vector3I.Min(min, raw.Min);
                    max = Vector3I.Max(max, raw.Max);
                    var block = new GridSnapshot.Block
                    {
                        Position = raw.Position,
                        Min = raw.Min,
                        Max = raw.Max,
                        Forward = raw.Forward,
                        Up = raw.Up,
                        Definition = raw.Definition,
                        Integrity = raw.Integrity,
                        Color = raw.Color,
                        Local = raw.Local
                    };
                    if (!identity)
                    {
                        block.Position = Cell(raw.Position, ref toPivot);
                        Vector3I a = Cell(raw.Min, ref toPivot);
                        Vector3I b = Cell(raw.Max, ref toPivot);
                        block.Min = Vector3I.Min(a, b);
                        block.Max = Vector3I.Max(a, b);
                        block.Forward = Direction(raw.Forward, ref toPivot);
                        block.Up = Direction(raw.Up, ref toPivot);
                    }
                    blocks[i] = block;
                }
                if (!identity)
                {
                    var toPivotFloat = (Matrix)toPivotMetres;
                    for (int i = 0; i < _locals.Count; i++)
                    {
                        GridSnapshot.Local local = _locals[i];
                        local.Matrix = local.Matrix * toPivotFloat;
                        _locals[i] = local;
                    }
                    for (int i = 0; i < _subparts.Count; i++)
                    {
                        GridSnapshot.Subpart subpart = _subparts[i];
                        subpart.Matrix = subpart.Matrix * toPivotFloat;
                        _subparts[i] = subpart;
                    }
                }
                snapshot.CapturedMin = min;
                snapshot.CapturedMax = max;
                var parts = new List<GridSnapshot>();
                for (int i = 0; i < _parts.Count; i++)
                {
                    GridSnapshot part = _parts[i].Result;
                    if (part == null)
                        continue;
                    part.ToParent = (Matrix)(_partToMain[i] * toPivotMetres);
                    parts.Add(part);
                }
                snapshot.Parts = parts.ToArray();
                snapshot.Blocks = blocks;
                snapshot.Definitions = _definitions.ToArray();
                snapshot.Models = _models.ToArray();
                snapshot.Colors = _colors.Count == 0 ? new[] { Vector3.Zero } : _colors.ToArray();
                snapshot.Locals = _locals.ToArray();
                snapshot.Subparts = _subparts.ToArray();
                _snapshot = snapshot;
            }
            catch (Exception exception)
            {
                // Caught on the worker: the engine may treat uncaught background exceptions as fatal.
                Error = exception.Message;
            }
            _blocks.Clear();
            _done = true;
        }

        internal static Vector3I Cell(Vector3I cell, ref MatrixD matrix)
        {
            return Vector3I.Round(Vector3D.Transform(new Vector3D(cell), matrix));
        }

        internal static Base6Directions.Direction Direction(Base6Directions.Direction direction, ref MatrixD matrix)
        {
            return Base6Directions.GetClosestDirection(
                (Vector3)Vector3D.TransformNormal(Base6Directions.GetVector(direction), matrix));
        }
    }
}
