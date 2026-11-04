using System.Collections.Generic;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// What a <see cref="GridSceneRenderer"/> draws: one grid seen by an orbital camera around its centre,
    /// optionally cut open and compared with a reference grid. Axes are the grid's own: X is its Right, Y its Up
    /// and Z its Backward, so the view follows the grid however it moves.
    /// </summary>
    public sealed class GridSceneState
    {
        /// <summary>The grid drawn; zero or an unknown id draws nothing.</summary>
        public long GridId;

        /// <summary>
        /// A grid to compare against (a projector's projection): its blocks with no matching block at the same
        /// world position in <see cref="GridId"/> are the missing ones. Zero compares nothing.
        /// </summary>
        public long ReferenceGridId;

        /// <summary>
        /// Drawn instead of the grid while <see cref="GridId"/> finds none (out of range, destroyed): the grid as
        /// it was last seen. Its blocks below full integrity show as damaged.
        /// </summary>
        public GridSnapshot MemorySnapshot;

        /// <summary>
        /// Compared against while <see cref="ReferenceGridId"/> finds none: the grid as it should be. Its blocks
        /// with no matching block in the drawn grid are the missing ones; blocks less whole than in it are damaged
        /// (see <see cref="GridSceneRenderer.ReferenceMismatch"/> when it does not fit the grid).
        /// </summary>
        public GridSnapshot ReferenceSnapshot;

        /// <summary>Turn around the grid's Up, in radians; zero puts the camera on the grid's Backward (+Z) side.</summary>
        public float Yaw;

        /// <summary>Camera elevation, in radians from -pi/2 (below) to pi/2 (above).</summary>
        public float Pitch;

        /// <summary>Screen rotation around the view direction, in radians, clockwise.</summary>
        public float Roll;

        /// <summary>One frames the whole grid; larger moves the camera closer.</summary>
        public float Zoom = 1f;

        /// <summary>
        /// Where the camera orbits, from the grid's centre, in metres along the grid's axes (see
        /// <see cref="GridSceneRenderer.Pan"/>).
        /// </summary>
        public Vector3 Pan;

        /// <summary>
        /// The kept part of the grid's box per axis, as fractions from its minimum (0) to its maximum (1) side;
        /// what lies outside is cut away and the section shows the inside.
        /// </summary>
        public Vector3 CutMinimum = Vector3.Zero;

        /// <summary>See <see cref="CutMinimum"/>.</summary>
        public Vector3 CutMaximum = Vector3.One;

        /// <summary>Draws the reference grid's missing blocks in <see cref="MissingColor"/>.</summary>
        public bool HighlightMissing = true;

        /// <summary>Color of the missing blocks.</summary>
        public Color MissingColor = new Color(220, 40, 40);

        /// <summary>How the missing blocks draw. A missing block has no real block in its place to draw instead.</summary>
        public GridHighlightStyle MissingStyle = GridHighlightStyle.Solid;

        /// <summary>
        /// Draws blocks below their full integrity (damaged, or not completely welded) in
        /// <see cref="DamagedColor"/>, darker the less damaged they are.
        /// </summary>
        public bool HighlightDamaged = true;

        /// <summary>Color of the damaged blocks.</summary>
        public Color DamagedColor = new Color(255, 150, 0);

        /// <summary>How the damaged blocks draw. With a wireframe the block itself keeps its own paint.</summary>
        public GridHighlightStyle DamagedStyle = GridHighlightStyle.Solid;

        /// <summary>
        /// Shades the damaged blocks darker the less damaged they are; false draws every damaged block in
        /// <see cref="DamagedColor"/> as is.
        /// </summary>
        public bool ShadeDamage = true;

        /// <summary>
        /// Leaves the damaged blocks unhighlighted for now without changing <see cref="HighlightDamaged"/>: toggled
        /// by the caller, the highlight flashes.
        /// </summary>
        public bool HideDamaged;

        /// <summary>
        /// The X-ray view's categories, one bit each (<see cref="CategoryBit"/>); zero is no X-ray. The grid draws
        /// dimmed in <see cref="XRayHullColor"/>, and its blocks of these categories draw over it in their own
        /// colors (<see cref="SetXRayColor"/>), seen through the hull wherever they are.
        /// </summary>
        public int XRayCategories;

        readonly Color[] _xRayColors =
        {
            Color.Gray,
            new Color(0, 64, 255),
            new Color(0, 255, 0),
            new Color(255, 255, 0),
            new Color(255, 64, 0),
            new Color(160, 40, 220),
            new Color(0, 170, 110),
            new Color(220, 160, 40),
            new Color(230, 60, 180)
        };

        // Changes with every SetXRayColor, so a renderer knows to rebuild its colors.
        internal int XRayColorsVersion;

        /// <summary>The bit of a category in <see cref="XRayCategories"/>.</summary>
        public static int CategoryBit(GridBlockCategory category)
        {
            return 1 << (int)category;
        }

        /// <summary>The color a category draws in under the X-ray.</summary>
        public Color GetXRayColor(GridBlockCategory category)
        {
            int index = (int)category;
            return index >= 0 && index < _xRayColors.Length ? _xRayColors[index] : Color.Gray;
        }

        /// <summary>Sets the color a category draws in under the X-ray.</summary>
        public void SetXRayColor(GridBlockCategory category, Color color)
        {
            int index = (int)category;
            if (index < 0 || index >= _xRayColors.Length || _xRayColors[index] == color)
                return;
            _xRayColors[index] = color;
            XRayColorsVersion++;
        }

        /// <summary>Color of the rest of the grid under an X-ray.</summary>
        public Color XRayHullColor = new Color(60, 64, 72);

        /// <summary>
        /// Keeps the damaged blocks highlighted under X-ray (<see cref="HighlightDamaged"/>): those of the X-ray's
        /// categories in their damage color over the category's, the rest of the hull's scaled by
        /// <see cref="XRayHullDamageSaturation"/> and <see cref="XRayHullDamageValue"/>. False hides the damage
        /// under X-ray.
        /// </summary>
        public bool XRayShowsDamage;

        /// <summary>Scales the saturation of the hull's damage color under X-ray (1: as is).</summary>
        public float XRayHullDamageSaturation = 1f;

        /// <summary>Scales the value (brightness) of the hull's damage color under X-ray (1: as is).</summary>
        public float XRayHullDamageValue = 1f;

        /// <summary>
        /// Draws the missiles flying around the grid: each a small diamond with its trail, and the path it is
        /// predicted to take to the grid it is closing on. The host keeps frames coming while
        /// <see cref="GridSceneRenderer.UpdateMissiles"/> says so.
        /// </summary>
        public bool ShowMissiles = true;

        internal struct Marker
        {
            public Vector3D Position;
            public Vector3 Velocity;
            public Color Color;
        }

        // By grid (zero: the drawn grid), then block position; replaced whole on change, so frames keep their copy.
        internal Dictionary<long, Dictionary<Vector3I, Vector3>> BlockHighlights =
            new Dictionary<long, Dictionary<Vector3I, Vector3>>();
        internal int BlockHighlightsVersion;
        internal readonly List<Marker> Markers = new List<Marker>();

        /// <summary>
        /// How the blocks of <see cref="HighlightBlock"/> draw. A filled style (not a wireframe) colors the block
        /// itself, and on a damaged block takes <see cref="DamagedStyle"/>.
        /// </summary>
        public GridHighlightStyle BlockHighlightStyle = GridHighlightStyle.SeeThroughWireframe;

        /// <summary>
        /// Highlights one block in a color of the caller's choosing (a hit, a block to look at), over the other
        /// highlights. <paramref name="gridId"/> is zero for the drawn grid, or a subgrid's entity id; the position is
        /// the block's (IMySlimBlock.Position, or a position from <see cref="GridSceneRenderer.GetLastHits"/>). A block
        /// the grid does not have draws nothing.
        /// </summary>
        public void HighlightBlock(long gridId, Vector3I position, Color color)
        {
            var highlights = new Dictionary<long, Dictionary<Vector3I, Vector3>>(BlockHighlights);
            Dictionary<Vector3I, Vector3> blocks;
            blocks = highlights.TryGetValue(gridId, out blocks)
                ? new Dictionary<Vector3I, Vector3>(blocks)
                : new Dictionary<Vector3I, Vector3>();
            blocks[position] = color.ToVector3();
            highlights[gridId] = blocks;
            BlockHighlights = highlights;
            BlockHighlightsVersion++;
        }

        /// <summary>Removes every <see cref="HighlightBlock"/> highlight.</summary>
        public void ClearBlockHighlights()
        {
            if (BlockHighlights.Count == 0)
                return;
            BlockHighlights = new Dictionary<long, Dictionary<Vector3I, Vector3>>();
            BlockHighlightsVersion++;
        }

        /// <summary>
        /// Marks a point in the world (an incoming missile) with a small diamond seen through everything, and a line
        /// along its velocity (metres per second) showing where it goes in the next second. Only drawn with the live
        /// grid. Cleared with <see cref="ClearMarkers"/>; a client refills them every update.
        /// </summary>
        public void AddMarker(Vector3D position, Vector3 velocity, Color color)
        {
            Markers.Add(new Marker { Position = position, Velocity = velocity, Color = color });
        }

        /// <summary>Removes every marker.</summary>
        public void ClearMarkers()
        {
            Markers.Clear();
        }
    }
}
