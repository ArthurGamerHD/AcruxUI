using System;
using System.Collections.Generic;
using Acrux.Render3D.Blocks;
using Acrux.Render3D.Missiles;
using Acrux.Rendering;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// Renders one grid in 3D around its centre (a ship preview): the block layer of the map's 3D scenes, on a
    /// transparent background, with an orbital camera, a cut-open section and highlighted damaged and missing
    /// blocks (see <see cref="GridSceneState"/>). Frames paint on the workers like the map's.
    /// </summary>
    public sealed class GridSceneRenderer : IDisposable
    {
        /// <summary>Smallest <see cref="GridSceneState.Zoom"/> the wheel reaches.</summary>
        public const float MINIMUM_ZOOM = 0.5f;

        /// <summary>Largest <see cref="GridSceneState.Zoom"/> the wheel reaches.</summary>
        public const float MAXIMUM_ZOOM = 16f;

        const float ZOOM_STEP = 1.2f;
        const float ORBIT_SENSITIVITY = 0.01f;
        const double FIELD_OF_VIEW_RADIANS = Math.PI / 4.5d;
        const double NEAR_DEPTH_METERS = 0.05d;
        const float CUT_EPSILON = 0.0001f;
        const float MINIMUM_DAMAGE_SHADE = 0.55f;
        const double MAXIMUM_PAN_RADII = 1.5d;

        sealed class FrameSlot
        {
            public float[] DepthBuffer;
            public readonly BlockLayer Blocks = new BlockLayer();
            public readonly BlockLayer XRay = new BlockLayer();
        }

        readonly SceneFramePipeline<FrameSlot> _pipeline = new SceneFramePipeline<FrameSlot>(() => new FrameSlot());
        readonly GridInspection _inspection = new GridInspection();
        readonly List<BlockGridView> _views = new List<BlockGridView>();
        BlockLayer _lastLayer;
        BlockLayer _lastXRay;
        bool _disposed;

        // Highlight colors, rebuilt when the inspection publishes or the state's colors change.
        Dictionary<Vector3I, Vector3> _damageColors;
        Dictionary<Vector3I, Vector3> _missingColors;
        Dictionary<Vector3I, Vector3> _xRayColors;
        // The same per subgrid, by entity id: the grid's own subgrids, and the reference snapshot's for missing.
        Dictionary<long, Dictionary<Vector3I, Vector3>> _partDamageColors;
        Dictionary<long, Dictionary<Vector3I, Vector3>> _partMissingColors;
        Dictionary<long, Dictionary<Vector3I, Vector3>> _partXRayColors;
        readonly List<Piece> _pieces = new List<Piece>();
        readonly List<IMyCubeGrid> _group = new List<IMyCubeGrid>();
        readonly MissileTracks _missiles = new MissileTracks();
        bool _hadMissiles;
        int _colorsVersion = -1;
        Color _damageColor;
        bool _shadeDamage = true;
        Color _missingColor;
        int _xRayCategories;
        int _xRayColorsVersion;

        /// <summary>Creates a renderer; dispose it with its view.</summary>
        public GridSceneRenderer()
        {
            _pipeline.Failed = failure => Error = failure.Message;
        }

        /// <summary>The last failed frame's error, or null.</summary>
        public string Error { get; private set; }

        /// <summary>The grid of the last update exists, or its memory snapshot stands in for it, and is drawn.</summary>
        public bool HasGrid { get; private set; }

        /// <summary>The grid is not found and its <see cref="GridSceneState.MemorySnapshot"/> is drawn instead.</summary>
        public bool IsShowingMemory { get; private set; }

        /// <summary>
        /// The <see cref="GridSceneState.ReferenceSnapshot"/> could not be placed on the grid as of the last finished
        /// scan: it was taken of another grid, or too little of it is left. Nothing shows as missing then.
        /// </summary>
        public bool ReferenceMismatch
        {
            get { return _inspection.ReferenceMismatch; }
        }

        /// <summary>Ints per block in <see cref="GetMapCells"/>.</summary>
        public const int MAP_CELL_STRIDE = GridInspection.CELL_STRIDE;

        /// <summary>Flag of a <see cref="GetMapCells"/> block missing from the grid.</summary>
        public const int MAP_CELL_MISSING = GridInspection.CELL_MISSING;

        /// <summary>Flag of a <see cref="GetMapCells"/> block less whole than in the reference.</summary>
        public const int MAP_CELL_DAMAGED = GridInspection.CELL_DAMAGED;

        /// <summary>
        /// Changes whenever a scan publishes new results (<see cref="GetMapCells"/> among them); zero before the first.
        /// </summary>
        public int ScanVersion
        {
            get { return _inspection.Version; }
        }

        /// <summary>
        /// For a flat map of the comparison, as of the last finished scan: the reference's blocks (the grid's own
        /// without a reference), <see cref="MAP_CELL_STRIDE"/> ints each: min x, y, z, max x, y, z in the drawn grid's
        /// cells and axes, MAP_CELL_ flags, and the <see cref="GridBlockCategory"/>. Null before the first scan.
        /// Shared and never changed: do not write to it.
        /// </summary>
        public int[] GetMapCells()
        {
            return _inspection.Cells;
        }

        /// <summary>Blocks of the reference grid missing from the grid, as of the last finished scan.</summary>
        public int MissingCount { get; private set; }

        /// <summary>Blocks of the grid below their full integrity, as of the last finished scan.</summary>
        public int DamagedCount { get; private set; }

        /// <summary>
        /// True while the first scan of the grid runs or the last frame waits on block models: a host shows it
        /// as loading and keeps requesting frames until it clears.
        /// </summary>
        public bool IsSceneLoading
        {
            get
            {
                return HasGrid && (_inspection.IsScanning || (_lastLayer != null && _lastLayer.PendingWork != null) ||
                                   (_lastXRay != null && _lastXRay.PendingWork != null));
            }
        }

        /// <summary>
        /// Game thread, every tick: advances the scan of damaged and missing blocks. True when it finished a pass
        /// with new results, so a new frame should be requested.
        /// </summary>
        public bool Update(GridSceneState state)
        {
            IMyCubeGrid grid = FindGrid(state == null ? 0 : state.GridId);
            GridSnapshot memory = grid == null && state != null ? state.MemorySnapshot : null;
            HasGrid = grid != null || memory != null;
            IsShowingMemory = memory != null;
            IMyCubeGrid reference = HasGrid ? FindGrid(state.ReferenceGridId) : null;
            if (!_inspection.Update(grid, memory, reference, HasGrid ? state.ReferenceSnapshot : null))
                return false;
            DamagedCount = _inspection.DamagedCount;
            MissingCount = _inspection.MissingCount;
            return true;
        }

        /// <summary>Missiles look this far past the grid's own radius, in metres.</summary>
        public const double MISSILE_RANGE_METRES = 3000d;

        /// <summary>
        /// Game thread, every tick while the 3D view shows: follows the missiles around the grid
        /// (<see cref="GridSceneState.ShowMissiles"/>). True while there are any, or the last just went: request a
        /// frame then.
        /// </summary>
        public bool UpdateMissiles(GridSceneState state)
        {
            IMyCubeGrid grid = state == null || !state.ShowMissiles ? null : FindGrid(state.GridId);
            if (grid == null)
                _missiles.Clear();
            else
                _missiles.Sample(new BoundingSphereD(grid.WorldVolume.Center, grid.WorldVolume.Radius + MISSILE_RANGE_METRES));
            bool any = _missiles.Count > 0;
            bool changed = any || _hadMissiles;
            _hadMissiles = any;
            return changed;
        }

        /// <summary>Missiles followed as of the last <see cref="UpdateMissiles"/>.</summary>
        public int MissileCount
        {
            get { return _missiles.Count; }
        }

        /// <summary>
        /// The blocks hit (lost integrity, or destroyed) between the last two scans of the live grid, four longs each:
        /// the grid (zero for the drawn grid, else a subgrid's entity id) and the block's position x, y, z, as
        /// <see cref="GridSceneState.HighlightBlock"/> takes them. Empty for a memory snapshot; null before two scans.
        /// Changes with <see cref="ScanVersion"/>. Shared: do not write to it.
        /// </summary>
        public long[] GetLastHits()
        {
            return _inspection.Hits;
        }

        /// <summary>Paints a frame of the state on the calling thread.</summary>
        public void Render(PixelCanvas canvas, GridSceneState state)
        {
            _pipeline.Render(canvas, slot => PrepareFrame(slot, canvas, state));
        }

        /// <summary>
        /// Starts painting a frame of the state on the workers, up to two in flight. Completions run in request
        /// order on the caller's thread. False when refused (two already painting): ask again on a later tick.
        /// </summary>
        public bool RequestRender(PixelCanvas canvas, GridSceneState state, Action<Exception> completed)
        {
            return !_disposed && _pipeline.Request(canvas, slot => PrepareFrame(slot, canvas, state), completed);
        }

        /// <summary>
        /// Caller's thread, outside any draw: delivers the completions of the frames that finished painting
        /// without waiting a tick for the game's callbacks. True when one was delivered.
        /// </summary>
        public bool CompleteFinishedRender()
        {
            return _pipeline.CompleteFinished();
        }

        /// <summary>Drag input in pixels: horizontal turns the camera around the grid, vertical tilts it.</summary>
        public void Orbit(GridSceneState state, Vector2 delta)
        {
            if (state == null)
                return;
            state.Yaw = MathHelper.WrapAngle(state.Yaw - delta.X * ORBIT_SENSITIVITY);
            state.Pitch = MathHelper.Clamp(state.Pitch + delta.Y * ORBIT_SENSITIVITY,
                -MathHelper.PiOver2, MathHelper.PiOver2);
        }

        /// <summary>
        /// Middle-drag input in canvas pixels: slides the orbit target across the view so the grid follows the
        /// pointer. <paramref name="canvasShortSide"/> is the shorter side of the canvas the frames are drawn to.
        /// </summary>
        public void Pan(GridSceneState state, Vector2 delta, float canvasShortSide)
        {
            Subject subject;
            if (!TryGetSubject(state, out subject) || canvasShortSide <= 0f)
                return;
            int side = Math.Max(1, (int)canvasShortSide);
            double radius = subject.Radius;
            SceneCamera camera = BuildCamera(state, radius, side, side);
            double metresPerPixel = Vector3D.Distance(camera.Position, (Vector3D)state.Pan) / camera.FocalLengthPixels;
            Vector3D pan = (Vector3D)state.Pan - camera.Right * (delta.X * metresPerPixel) +
                           camera.Up * (delta.Y * metresPerPixel);
            // Never so far that the grid leaves the view for good.
            double limit = radius * MAXIMUM_PAN_RADII;
            if (pan.LengthSquared() > limit * limit)
                pan = Vector3D.Normalize(pan) * limit;
            state.Pan = (Vector3)pan;
        }

        /// <summary>
        /// The zoom at which the grid, seen from the state's yaw, pitch and roll, fills the view's shorter side the
        /// way a flat map of it would (its longer side across, its near face inside the view). One is the default
        /// framing of the whole grid from any side. A client sets it when it points the camera at a side (a locked
        /// view), not while the user orbits, so the camera never moves on its own. One when the grid is unknown.
        /// </summary>
        public float GetFitZoom(GridSceneState state)
        {
            Subject subject;
            if (!TryGetSubject(state, out subject))
                return 1f;
            Vector3D half = subject.Box.HalfExtents;
            SceneCamera camera = BuildCamera(state, subject.Radius, 1, 1);
            double halfWidth = Extent(camera.Right, half);
            double halfHeight = Extent(camera.Up, half);
            double halfDepth = Extent(camera.Forward, half);
            double halfAngle = FIELD_OF_VIEW_RADIANS * 0.5d;
            double fitDistance = Math.Max(halfWidth, halfHeight) / Math.Tan(halfAngle) + halfDepth;
            double sphereDistance = subject.Radius / Math.Sin(halfAngle);
            if (!(fitDistance > 0d))
                return 1f;
            return MathHelper.Clamp((float)(sphereDistance / fitDistance), MINIMUM_ZOOM, MAXIMUM_ZOOM);
        }

        // Half the box's size along a direction.
        static double Extent(Vector3D axis, Vector3D half)
        {
            return Math.Abs(axis.X) * half.X + Math.Abs(axis.Y) * half.Y + Math.Abs(axis.Z) * half.Z;
        }

        /// <summary>Wheel input: positive moves the camera closer. False at the zoom limit.</summary>
        public bool Scroll(GridSceneState state, int delta)
        {
            if (state == null || delta == 0)
                return false;
            float zoom = IsFinite(state.Zoom) && state.Zoom > 0f ? state.Zoom : 1f;
            float next = MathHelper.Clamp(delta > 0 ? zoom * ZOOM_STEP : zoom / ZOOM_STEP, MINIMUM_ZOOM, MAXIMUM_ZOOM);
            if (Math.Abs(next - zoom) <= 0.0001f)
                return false;
            state.Zoom = next;
            return true;
        }

        SceneFrame PrepareFrame(FrameSlot slot, PixelCanvas canvas, GridSceneState state)
        {
            var frame = new SceneFrame();
            _lastLayer = null;
            _lastXRay = null;
            int width = canvas.Width;
            int height = canvas.Height;
            Subject subject;
            if (!TryGetSubject(state, out subject) || width <= 0 || height <= 0)
                return frame;

            BoundingBoxD box = subject.Box;
            Vector3D center = box.Center;
            double radius = subject.Radius;
            MatrixD worldToScene = MatrixD.Invert(subject.DisplayToWorld) * MatrixD.CreateTranslation(-center);

            SceneCamera camera = BuildCamera(state, radius, width, height);
            SceneCut cut = BuildCut(state, box, center);
            Vector3 light = Vector3.Normalize((Vector3)(camera.Up * 0.7d - camera.Right * 0.3d - camera.Forward * 0.65d));
            RefreshColors(state);

            bool xRay = (state.XRayCategories & ~GridSceneState.CategoryBit(GridBlockCategory.None)) != 0;
            bool damaged = (!xRay || state.XRayShowsDamage) && state.HighlightDamaged && !state.HideDamaged;
            // An outline leaves the block its own paint: the grid draws plainly, and a view of only the damaged
            // blocks (buried ones too, for a see-through outline) adds the wires.
            bool damagedWire = IsWire(state.DamagedStyle);
            // A filled block highlight colors the block itself, as the damage does: a second copy of the block on
            // top would fight it for the same depth.
            bool filledHighlights = !IsWire(state.BlockHighlightStyle) && state.BlockHighlights.Count > 0;
            CollectPieces(subject);
            _views.Clear();
            for (int p = 0; p < _pieces.Count; p++)
            {
                Piece piece = _pieces[p];
                Dictionary<Vector3I, Vector3> damage = damaged ? DamageColorsOf(piece.Id) : null;
                if (xRay && damage != null)
                    damage = XRayDamageOf(piece.Id, damage, state).Hull;
                BlockGridView view = piece.View;
                view.Highlights = damage != null && !damagedWire ? damage : null;
                view.HighlightStyle = state.DamagedStyle;
                Dictionary<Vector3I, Vector3> blocks;
                if (filledHighlights && state.BlockHighlights.TryGetValue(piece.Id, out blocks) && blocks.Count > 0)
                {
                    if (view.Highlights == null)
                    {
                        view.Highlights = blocks;
                        view.HighlightStyle = state.BlockHighlightStyle;
                    }
                    else
                    {
                        view.Highlights = Merge(view.Highlights, blocks);
                    }
                }
                view.HasTint = xRay;
                view.Tint = state.XRayHullColor.ToVector3();
                _views.Add(view);
                if (damage == null || !damagedWire)
                    continue;
                view = piece.View;
                view.Highlights = damage;
                view.HighlightedOnly = true;
                view.IncludeEnclosed = true;
                view.HighlightStyle = state.DamagedStyle;
                _views.Add(view);
            }
            if (!xRay && state.HighlightMissing)
                AddMissingViews(state, subject);
            AddBlockHighlightViews(state);
            var capture = new SceneCapture
            {
                Camera = camera,
                WorldToScene = worldToScene,
                CenterWorld = Vector3D.Transform(center, subject.DisplayToWorld),
                Radius = radius,
                Light = light
            };
            BlockLayer layer = slot.Blocks;
            layer.Capture(ref capture, _views, cut);
            List<SceneLine> lines = subject.Grid == null ? null : MarkerLines(state, camera, worldToScene);
            if (lines != null && state.ShowMissiles)
                _missiles.AddLines(lines, camera, worldToScene);
            layer.SetLines(lines);
            _views.Clear();
            _lastLayer = layer;

            frame.Initialize = () => frame.DepthBuffer = SceneFrame.ClearDepth(ref slot.DepthBuffer, width, height);
            frame.Builds.Add(layer.Build);
            frame.Commands.Add((target, first, end) => layer.Draw(target, frame.DepthBuffer, first, end));
            if (xRay)
            {
                for (int p = 0; p < _pieces.Count; p++)
                {
                    Dictionary<Vector3I, Vector3> colors = XRayColorsOf(_pieces[p].Id);
                    if (colors == null)
                        continue;
                    // A filled damage overlay colors the category's damaged blocks; an outline draws over them anyway.
                    Dictionary<Vector3I, Vector3> damage = damaged && !damagedWire ? DamageColorsOf(_pieces[p].Id) : null;
                    if (damage != null)
                        colors = XRayDamageOf(_pieces[p].Id, damage, state).Category;
                    BlockGridView view = _pieces[p].View;
                    view.Highlights = colors;
                    view.HighlightedOnly = true;
                    view.IncludeEnclosed = true;
                    _views.Add(view);
                }
            }
            if (_views.Count > 0)
            {
                BlockLayer category = slot.XRay;
                category.Capture(ref capture, _views, cut);
                _views.Clear();
                _lastXRay = category;
                frame.Builds.Add(category.Build);
                // The hull's depth is forgotten, so the category draws over it wherever it is inside.
                frame.Commands.Add((target, first, end) =>
                {
                    ClearDepthRows(frame.DepthBuffer, width, first, end);
                    category.Draw(target, frame.DepthBuffer, first, end);
                });
            }
            frame.Commands.Add((target, first, end) => layer.DrawSeeThrough(target, first, end));
            return frame;
        }

        // One grid the scene draws: the subject (Id zero) or one of its subgrids (its entity id).
        struct Piece
        {
            public long Id;
            public BlockGridView View;
        }

        // The subject and its subgrids: the live grid's mechanical group, or the memory snapshot's parts where they
        // were when it was taken.
        void CollectPieces(Subject subject)
        {
            _pieces.Clear();
            _pieces.Add(new Piece { View = subject.View() });
            if (subject.Grid != null)
            {
                if (MyAPIGateway.GridGroups == null)
                    return;
                _group.Clear();
                MyAPIGateway.GridGroups.GetGroup(subject.Grid, GridLinkTypeEnum.Mechanical, _group);
                for (int i = 0; i < _group.Count; i++)
                {
                    IMyCubeGrid grid = _group[i];
                    if (grid != null && grid != subject.Grid && !grid.MarkedForClose)
                        _pieces.Add(new Piece { Id = grid.EntityId, View = new BlockGridView { Grid = grid } });
                }
                _group.Clear();
                return;
            }
            GridSnapshot[] parts = subject.Memory.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                _pieces.Add(new Piece
                {
                    Id = parts[i].GridEntityId,
                    View = new BlockGridView
                    {
                        Snapshot = parts[i],
                        SnapshotToWorld = (MatrixD)parts[i].ToParent * subject.NativeToWorld
                    }
                });
            }
        }

        // The reference's missing blocks: a live projection in place, or the reference snapshot placed on the grid,
        // its subgrids on the drawn subgrid of the same id (where it is now) or else where they were.
        void AddMissingViews(GridSceneState state, Subject subject)
        {
            IMyCubeGrid reference = subject.Grid == null ? null : FindGrid(state.ReferenceGridId);
            bool memory = reference == null && state.ReferenceSnapshot != null && _inspection.HasReference;
            var view = new BlockGridView
            {
                HighlightedOnly = true,
                IncludeEnclosed = IsWire(state.MissingStyle),
                HighlightStyle = state.MissingStyle
            };
            if (reference != null && _missingColors != null)
            {
                view.Grid = reference;
                view.Highlights = _missingColors;
                _views.Add(view);
                return;
            }
            if (!memory)
                return;
            MatrixD referenceToWorld = GridInspection.Metres(_inspection.ReferenceToGrid, subject.GridSize) * subject.NativeToWorld;
            if (_missingColors != null)
            {
                view.Snapshot = state.ReferenceSnapshot;
                view.SnapshotToWorld = referenceToWorld;
                view.Highlights = _missingColors;
                _views.Add(view);
            }
            if (_partMissingColors == null)
                return;
            GridSnapshot[] parts = state.ReferenceSnapshot.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                Dictionary<Vector3I, Vector3> colors;
                if (!_partMissingColors.TryGetValue(parts[i].GridEntityId, out colors))
                    continue;
                IMyCubeGrid live = subject.Grid == null ? null : FindGrid(parts[i].GridEntityId);
                view.Snapshot = parts[i];
                view.SnapshotToWorld = live != null ? live.WorldMatrix : (MatrixD)parts[i].ToParent * referenceToWorld;
                view.Highlights = colors;
                _views.Add(view);
            }
        }

        void AddBlockHighlightViews(GridSceneState state)
        {
            Dictionary<long, Dictionary<Vector3I, Vector3>> highlights = state.BlockHighlights;
            if (highlights.Count == 0 || !IsWire(state.BlockHighlightStyle))
                return;
            for (int p = 0; p < _pieces.Count; p++)
            {
                Dictionary<Vector3I, Vector3> blocks;
                if (!highlights.TryGetValue(_pieces[p].Id, out blocks) || blocks.Count == 0)
                    continue;
                BlockGridView view = _pieces[p].View;
                view.Highlights = blocks;
                view.HighlightedOnly = true;
                view.IncludeEnclosed = true;
                view.HighlightStyle = state.BlockHighlightStyle;
                _views.Add(view);
            }
        }

        // The damage with the block highlights over it, in the damage's style.
        static Dictionary<Vector3I, Vector3> Merge(Dictionary<Vector3I, Vector3> damage,
            Dictionary<Vector3I, Vector3> blocks)
        {
            var merged = new Dictionary<Vector3I, Vector3>(damage);
            foreach (KeyValuePair<Vector3I, Vector3> pair in blocks)
                merged[pair.Key] = pair.Value;
            return merged;
        }

        const double MARKER_PIXELS = 7d;
        readonly List<SceneLine> _markerLines = new List<SceneLine>();

        // Each marker a diamond of about the same size on screen at any distance, and its next second of travel.
        List<SceneLine> MarkerLines(GridSceneState state, SceneCamera camera, MatrixD worldToScene)
        {
            _markerLines.Clear();
            List<GridSceneState.Marker> markers = state.Markers;
            for (int i = 0; i < markers.Count; i++)
            {
                GridSceneState.Marker marker = markers[i];
                Vector3D at = Vector3D.Transform(marker.Position, worldToScene);
                double size = Vector3D.Distance(at, camera.Position) * MARKER_PIXELS / camera.FocalLengthPixels;
                Vector3D right = camera.Right * size;
                Vector3D up = camera.Up * size;
                Vector3D toward = camera.Forward * size;
                var corners = new[] { at + right, at + up, at - right, at - up, at + toward, at - toward };
                for (int c = 0; c < 4; c++)
                {
                    Line(corners[c], corners[(c + 1) % 4], marker.Color);
                    Line(corners[c], corners[4], marker.Color);
                    Line(corners[c], corners[5], marker.Color);
                }
                Vector3D velocity = Vector3D.TransformNormal((Vector3D)marker.Velocity, worldToScene);
                if (velocity.LengthSquared() > 0.01d)
                    Line(at, at + velocity, marker.Color);
            }
            return _markerLines;
        }

        void Line(Vector3D from, Vector3D to, Color color)
        {
            _markerLines.Add(new SceneLine { From = from, To = to, Color = color });
        }

        Dictionary<Vector3I, Vector3> DamageColorsOf(long piece)
        {
            return ColorsOf(piece, _damageColors, _partDamageColors);
        }

        Dictionary<Vector3I, Vector3> XRayColorsOf(long piece)
        {
            return ColorsOf(piece, _xRayColors, _partXRayColors);
        }

        // The damage under X-ray (GridSceneState.XRayShowsDamage), by piece: on the hull scaled as the state says; in
        // a highlighted category in full, over the category's color. Rebuilt only when the damage or category colors
        // are (RefreshColors replaces them) or the scaling changes.
        sealed class XRayDamage
        {
            public Dictionary<Vector3I, Vector3> Damage;
            public Dictionary<Vector3I, Vector3> Categories;
            public float Saturation;
            public float Value;
            public Dictionary<Vector3I, Vector3> Hull;
            public Dictionary<Vector3I, Vector3> Category;
        }

        readonly Dictionary<long, XRayDamage> _xRayDamage = new Dictionary<long, XRayDamage>();

        XRayDamage XRayDamageOf(long piece, Dictionary<Vector3I, Vector3> damage, GridSceneState state)
        {
            Dictionary<Vector3I, Vector3> categories = XRayColorsOf(piece);
            float saturation = state.XRayHullDamageSaturation;
            float value = state.XRayHullDamageValue;
            XRayDamage cached;
            if (_xRayDamage.TryGetValue(piece, out cached) && ReferenceEquals(cached.Damage, damage) &&
                ReferenceEquals(cached.Categories, categories) && cached.Saturation == saturation &&
                cached.Value == value)
                return cached;
            cached = new XRayDamage
            {
                Damage = damage,
                Categories = categories,
                Saturation = saturation,
                Value = value,
                Hull = new Dictionary<Vector3I, Vector3>(damage.Count),
                Category = categories == null ? null : new Dictionary<Vector3I, Vector3>(categories)
            };
            foreach (KeyValuePair<Vector3I, Vector3> pair in damage)
            {
                if (categories != null && categories.ContainsKey(pair.Key))
                {
                    cached.Hull[pair.Key] = pair.Value;
                    cached.Category[pair.Key] = pair.Value;
                    continue;
                }
                Vector3 hsv = new Color(pair.Value).ColorToHSV();
                hsv.Y *= saturation;
                hsv.Z *= value;
                cached.Hull[pair.Key] = hsv.HSVtoColor().ToVector3();
            }
            _xRayDamage[piece] = cached;
            return cached;
        }

        static Dictionary<Vector3I, Vector3> ColorsOf(long piece, Dictionary<Vector3I, Vector3> main,
            Dictionary<long, Dictionary<Vector3I, Vector3>> parts)
        {
            if (piece == 0)
                return main;
            Dictionary<Vector3I, Vector3> colors;
            return parts != null && parts.TryGetValue(piece, out colors) ? colors : null;
        }

        // What the scene draws: the live grid, or its memory snapshot. Display is the grid's own axes (as captured,
        // for a snapshot), native the cells its blocks and highlights are kept in.
        struct Subject
        {
            public IMyCubeGrid Grid;
            public GridSnapshot Memory;
            public MatrixD DisplayToWorld;
            public MatrixD NativeToWorld;
            // In the display frame's metres.
            public BoundingBoxD Box;
            public float GridSize;
            public double Radius;

            public BlockGridView View()
            {
                return new BlockGridView { Grid = Grid, Snapshot = Memory, SnapshotToWorld = NativeToWorld };
            }
        }

        static bool TryGetSubject(GridSceneState state, out Subject subject)
        {
            subject = default(Subject);
            if (state == null)
                return false;
            IMyCubeGrid grid = FindGrid(state.GridId);
            if (grid != null)
            {
                subject.Grid = grid;
                subject.GridSize = grid.GridSize;
                subject.DisplayToWorld = grid.WorldMatrix;
                subject.NativeToWorld = grid.WorldMatrix;
                subject.Box = CellBox(grid.Min, grid.Max, grid.GridSize);
            }
            else
            {
                GridSnapshot memory = state.MemorySnapshot;
                if (memory == null || memory.BlockCount == 0)
                    return false;
                subject.Memory = memory;
                subject.GridSize = memory.GridSize;
                subject.DisplayToWorld = MatrixD.Identity;
                subject.NativeToWorld = GridSnapshot.Pose(memory.PivotPosition, memory.PivotForward, memory.PivotUp,
                    memory.GridSize);
                subject.Box = CellBox(memory.CapturedMin, memory.CapturedMax, memory.GridSize);
            }
            subject.Radius = Math.Max(subject.GridSize, subject.Box.HalfExtents.Length());
            return true;
        }

        static bool IsWire(GridHighlightStyle style)
        {
            return style == GridHighlightStyle.Wireframe || style == GridHighlightStyle.SeeThroughWireframe ||
                   style == GridHighlightStyle.ModelWireframe;
        }

        static void ClearDepthRows(float[] depth, int width, int firstRow, int endRow)
        {
            int end = Math.Min(depth.Length, endRow * width);
            for (int i = firstRow * width; i < end; i++)
                depth[i] = float.NegativeInfinity;
        }

        // Cells as metres, each cell centred on its index.
        static BoundingBoxD CellBox(Vector3I min, Vector3I max, double size)
        {
            return new BoundingBoxD(new Vector3D(min) * size - size * 0.5d, new Vector3D(max) * size + size * 0.5d);
        }

        static SceneCamera BuildCamera(GridSceneState state, double radius, int width, int height)
        {
            double yaw = IsFinite(state.Yaw) ? state.Yaw : 0d;
            double pitch = IsFinite(state.Pitch) ? MathHelper.Clamp(state.Pitch, -MathHelper.PiOver2, MathHelper.PiOver2) : 0d;
            double roll = IsFinite(state.Roll) ? state.Roll : 0d;
            double zoom = IsFinite(state.Zoom) && state.Zoom > 0f ? state.Zoom : 1d;

            // Target to camera, in the grid's axes.
            var toCamera = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw));
            Vector3D forward = -toCamera;
            // Defined by the yaw alone, so the view stays upright through straight up and down.
            var right = new Vector3D(Math.Cos(yaw), 0d, -Math.Sin(yaw));
            Vector3D up = Vector3D.Cross(right, forward);
            up.Normalize();
            double sine = Math.Sin(roll);
            double cosine = Math.Cos(roll);
            // Turning the camera's axes the other way turns the picture clockwise.
            Vector3D rolledRight = right * cosine + up * sine;
            Vector3D rolledUp = up * cosine - right * sine;

            double halfAngle = FIELD_OF_VIEW_RADIANS * 0.5d;
            double distance = radius / Math.Sin(halfAngle) / zoom;
            Vector3D target = IsFinite(state.Pan.X) && IsFinite(state.Pan.Y) && IsFinite(state.Pan.Z)
                ? (Vector3D)state.Pan
                : Vector3D.Zero;
            return new SceneCamera
            {
                Position = target + toCamera * distance,
                Right = rolledRight,
                Up = rolledUp,
                Forward = forward,
                FocalLengthPixels = Math.Min(width, height) * 0.5d / Math.Tan(halfAngle),
                CenterX = width * 0.5d,
                CenterY = height * 0.5d,
                NearDepth = NEAR_DEPTH_METERS,
                Width = width,
                Height = height
            };
        }

        static SceneCut BuildCut(GridSceneState state, BoundingBoxD box, Vector3D center)
        {
            Vector3 from = Vector3.Clamp(state.CutMinimum, Vector3.Zero, Vector3.One);
            Vector3 to = Vector3.Clamp(state.CutMaximum, Vector3.Zero, Vector3.One);
            int trimmed = 0;
            if (from.X > CUT_EPSILON) trimmed |= 1;
            if (from.Y > CUT_EPSILON) trimmed |= 2;
            if (from.Z > CUT_EPSILON) trimmed |= 4;
            if (to.X < 1f - CUT_EPSILON) trimmed |= 8;
            if (to.Y < 1f - CUT_EPSILON) trimmed |= 16;
            if (to.Z < 1f - CUT_EPSILON) trimmed |= 32;
            if (trimmed == 0)
                return default(SceneCut);
            Vector3D extent = box.Max - box.Min;
            return new SceneCut
            {
                Enabled = true,
                Minimum = box.Min + extent * (Vector3D)Vector3.Min(from, to) - center,
                Maximum = box.Min + extent * (Vector3D)Vector3.Max(from, to) - center,
                TrimmedSides = trimmed
            };
        }

        void RefreshColors(GridSceneState state)
        {
            if (_colorsVersion == _inspection.Version && _damageColor == state.DamagedColor &&
                _shadeDamage == state.ShadeDamage &&
                _missingColor == state.MissingColor && _xRayCategories == state.XRayCategories &&
                _xRayColorsVersion == state.XRayColorsVersion)
                return;
            _colorsVersion = _inspection.Version;
            _damageColor = state.DamagedColor;
            _shadeDamage = state.ShadeDamage;
            _missingColor = state.MissingColor;
            _xRayCategories = state.XRayCategories;
            _xRayColorsVersion = state.XRayColorsVersion;

            // New dictionaries: frames in flight keep reading the ones they captured.
            _xRayColors = XRayColors(state, _inspection.Categories);
            _damageColors = DamageColors(_inspection.Damage, _damageColor.ToVector3(), _shadeDamage);
            _missingColors = MissingColors(_inspection.Missing, _missingColor.ToVector3());
            _partXRayColors = null;
            _partDamageColors = null;
            _partMissingColors = null;
            if (_inspection.PartCategories != null)
            {
                foreach (KeyValuePair<long, List<Vector3I>[]> pair in _inspection.PartCategories)
                    Put(ref _partXRayColors, pair.Key, XRayColors(state, pair.Value));
            }
            if (_inspection.PartDamage != null)
            {
                foreach (KeyValuePair<long, Dictionary<Vector3I, float>> pair in _inspection.PartDamage)
                    Put(ref _partDamageColors, pair.Key, DamageColors(pair.Value, _damageColor.ToVector3(), _shadeDamage));
            }
            if (_inspection.PartMissing != null)
            {
                foreach (KeyValuePair<long, List<Vector3I>> pair in _inspection.PartMissing)
                    Put(ref _partMissingColors, pair.Key, MissingColors(pair.Value, _missingColor.ToVector3()));
            }
        }

        static void Put(ref Dictionary<long, Dictionary<Vector3I, Vector3>> parts, long id,
            Dictionary<Vector3I, Vector3> colors)
        {
            if (colors == null)
                return;
            if (parts == null)
                parts = new Dictionary<long, Dictionary<Vector3I, Vector3>>();
            parts[id] = colors;
        }

        Dictionary<Vector3I, Vector3> XRayColors(GridSceneState state, List<Vector3I>[] categories)
        {
            Dictionary<Vector3I, Vector3> colors = null;
            for (int index = 1; categories != null && index < categories.Length; index++)
            {
                var category = (GridBlockCategory)index;
                if ((_xRayCategories & GridSceneState.CategoryBit(category)) == 0 || categories[index].Count == 0)
                    continue;
                if (colors == null)
                    colors = new Dictionary<Vector3I, Vector3>();
                Vector3 color = state.GetXRayColor(category).ToVector3();
                List<Vector3I> positions = categories[index];
                for (int i = 0; i < positions.Count; i++)
                    colors[positions[i]] = color;
            }
            return colors;
        }

        static Dictionary<Vector3I, Vector3> DamageColors(Dictionary<Vector3I, float> damage, Vector3 color,
            bool shade)
        {
            if (damage == null || damage.Count == 0)
                return null;
            var colors = new Dictionary<Vector3I, Vector3>(damage.Count);
            foreach (KeyValuePair<Vector3I, float> pair in damage)
                colors[pair.Key] = shade
                    ? color * (MINIMUM_DAMAGE_SHADE + (1f - MINIMUM_DAMAGE_SHADE) * pair.Value)
                    : color;
            return colors;
        }

        static Dictionary<Vector3I, Vector3> MissingColors(List<Vector3I> missing, Vector3 color)
        {
            if (missing == null || missing.Count == 0)
                return null;
            var colors = new Dictionary<Vector3I, Vector3>(missing.Count);
            for (int i = 0; i < missing.Count; i++)
                colors[missing[i]] = color;
            return colors;
        }

        static IMyCubeGrid FindGrid(long id)
        {
            if (id == 0 || MyAPIGateway.Entities == null)
                return null;
            var grid = MyAPIGateway.Entities.GetEntityById(id) as IMyCubeGrid;
            return grid == null || grid.MarkedForClose || grid.Closed ? null : grid;
        }

        static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Stops new frames; frames in flight finish and still complete.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _pipeline.Dispose();
            _inspection.Reset();
            _damageColors = null;
            _missingColors = null;
            _xRayColors = null;
            _partDamageColors = null;
            _partMissingColors = null;
            _partXRayColors = null;
            _xRayDamage.Clear();
        }

        /// <summary>
        /// The block definition id ("Type/Subtype") the grid has most of in a category, as of the last finished scan;
        /// null when it has none. A client shows it as the category's icon (see Acrux.Sprites.BlockSprites).
        /// </summary>
        public string GetCommonBlock(GridBlockCategory category)
        {
            string[] common = _inspection.CommonBlocks;
            int index = (int)category;
            return common == null || index < 0 || index >= common.Length ? null : common[index];
        }

        /// <summary>Blocks of a category on the grid and its subgrids, as of the last finished scan.</summary>
        public int CountOf(GridBlockCategory category)
        {
            int index = (int)category;
            int count = CountIn(_inspection.Categories, index);
            if (_inspection.PartCategories != null)
            {
                foreach (List<Vector3I>[] categories in _inspection.PartCategories.Values)
                    count += CountIn(categories, index);
            }
            return count;
        }

        static int CountIn(List<Vector3I>[] categories, int index)
        {
            return categories == null || index <= 0 || index >= categories.Length ? 0 : categories[index].Count;
        }
    }
}
