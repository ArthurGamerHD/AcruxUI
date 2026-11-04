using System;
using Acrux.Render3D;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Cartography.Maps.Planet3D
{
    /// <summary>
    /// The scattering inputs of one planet's atmosphere, in planet-local metres. Colors are not chosen here:
    /// they follow from the planet's own Rayleigh coefficients, as the game's atmosphere renderer derives them
    /// (MyRenderComponentPlanet, MyAtmosphereRenderer): 1 / wavelength^4 per channel, divided by the
    /// definition's RayleighScattering.
    /// </summary>
    internal struct AtmosphereParameters
    {
        // The game's sea level, the base of the density profile.
        public double GroundRadius;
        public double TopRadius;
        // Relative per channel; the strongest is 1.
        public Vector3 Scattering;
        public Vector3 SunColor;
        // Planet-local, toward the sun.
        public Vector3 SunDirection;
        // Shell fraction over which density falls by e. Eases from the orbit view's thin glow at the top to the in-
        // air profile deeper down (ScaleHeightAt), so the two views meet.
        public double ScaleHeight;
        // MyAtmosphereSettings.Intensity: 1 by default, 6 on EarthLike.
        public float Intensity;
        // MyAtmosphereSettings.FogIntensity; 0 disables the fog.
        public float FogIntensity;
    }

    /// <summary>
    /// A fake single-scattering sky fill and distance fog over the finished frame.
    /// <para>
    /// Every pixel the renderer left as sky has its view ray marched through the atmosphere shell and is
    /// overlaid with one tint at one opacity: the opacity is how much of what lies behind the air hides (its
    /// mean extinction, or more where the scattered light is brighter than that), the tint is the sunlight
    /// scattered toward the camera, itself reddened by the air between each sample and the sun. Toward the
    /// zenith that reads blue; along a long, low sun path the planet's coefficients leave the warm sunset
    /// colors; on the night side the air only dims the stars.
    /// </para>
    /// <para>
    /// Every drawn pixel is post-processed with distance fog from the depth buffer: a lerp toward the horizon
    /// sky in its direction, taken from a small ring of horizon colors computed once per frame.
    /// </para>
    /// </summary>
    internal sealed class AtmosphereScene : ISceneLayer
    {
        const int SAMPLES = 6;
        // Shell-height fraction over which the air density falls by e.
        const double SCALE_HEIGHT = 0.25d;
        // Optical depth per shell height of air at ground density, for the strongest channel. Looking up
        // from the ground this leaves about half of the sky behind it.
        const float EXTINCTION = 3.2f;
        // The orbit view's glow: its scale height as a fraction of the shell.
        const double GLOBE_SCALE_HEIGHT = 0.06d;
        // Half-width of the shadow's soft edge, in planet radii.
        const float SHADOW_EDGE = 0.04f;
        // How much of the horizon the sun still lights after it sets, as a fraction of the shell.
        const float TWILIGHT = 0.08f;
        // Horizon directions the fog color ring holds.
        const int FOG_DIRECTIONS = 32;
        // Fog lengths per scale height of ground-density air at FogIntensity 1.
        const double FOG_STRENGTH = 1.5d;

        public SceneCamera Camera;
        public AtmosphereParameters Atmosphere;

        // The orbit view's glow at the top of the atmosphere (or at handoverRadius, whichever is lower), easing into
        // the in-air profile toward the ground.
        public static double ScaleHeightAt(double radius, double groundRadius, double topRadius, double handoverRadius)
        {
            double upper = Math.Min(topRadius, handoverRadius > groundRadius ? handoverRadius : topRadius);
            double span = upper - groundRadius;
            if (span <= 0d)
                return SCALE_HEIGHT;
            double height = MathHelper.Clamp((radius - groundRadius) / span, 0d, 1d);
            double t = MathHelper.Clamp((height - 0.5d) / 0.5d, 0d, 1d);
            t = t * t * (3d - 2d * t);
            return SCALE_HEIGHT + (GLOBE_SCALE_HEIGHT - SCALE_HEIGHT) * t;
        }

        public void Build()
        {
            _grids.Prepare(Camera.Height);
        }

        public string PendingWork
        {
            get { return null; }
        }

        // The fog ring, built by Prepare on the capturing thread and only read by the bands.
        readonly Color[] _fogColors = new Color[FOG_DIRECTIONS];
        readonly float[] _fogOpacities = new float[FOG_DIRECTIONS];
        Vector3D _fogEast;
        Vector3D _fogNorth;
        double _fogInverseDistance;

        // The fog ring: sky color and opacity looking level at the horizon. Off above the air and on planets without
        // fog.
        public void Prepare()
        {
            _fogInverseDistance = 0d;
            AtmosphereParameters air = Atmosphere;
            Vector3D origin = Camera.Position;
            double radius = origin.Length();
            double shell = air.TopRadius - air.GroundRadius;
            if (air.FogIntensity <= 0f || shell <= 0d || radius <= 1d || radius >= air.TopRadius)
                return;

            Vector3D up = origin / radius;
            Vector3D east = Vector3D.Cross(up, Math.Abs(up.Y) < 0.9d ? Vector3D.Up : Vector3D.Right);
            east.Normalize();
            _fogEast = east;
            _fogNorth = Vector3D.Cross(up, east);
            double top2 = air.TopRadius * air.TopRadius;
            for (int i = 0; i < FOG_DIRECTIONS; i++)
            {
                double angle = i * MathHelper.TwoPi / FOG_DIRECTIONS;
                Vector3D level = _fogEast * Math.Cos(angle) + _fogNorth * Math.Sin(angle);
                double b = Vector3D.Dot(origin, level);
                double exit = -b + Math.Sqrt(Math.Max(0d, b * b - (radius * radius - top2)));
                Vector3 transmittance;
                Vector3 scattered;
                March(ref air, origin, level, 0d, exit, shell, out transmittance, out scattered);
                float opacity;
                _fogColors[i] = Composite(ref air, transmittance, scattered, level, out opacity);
                _fogOpacities[i] = opacity;
            }

            // Fog thins with the air at the camera, so it fades out as a ship climbs.
            double density = Math.Exp(-Math.Max(0d, (radius - air.GroundRadius) / shell) / air.ScaleHeight);
            _fogInverseDistance = air.FogIntensity * FOG_STRENGTH * density / (shell * air.ScaleHeight);
        }

        // Screen pixels between the points where the sky and the fog are evaluated. Both change slowly across
        // the screen (the fog's direction terms, not its depth), so the points sit on a grid of this spacing
        // and every pixel blends the four around it bilinearly; depth stays per pixel.
        const int GRID = 4;
        const int GRID_SHIFT = 2;
        const byte POINT_SKY = 1;
        const byte POINT_FOG = 2;

        /// <summary>One grid point: the sky through it (premultiplied, 0..255) and the fog's direction terms.</summary>
        struct GridPoint
        {
            public byte Flags;
            public Vector3 Sky;
            public float SkyOpacity;
            public Vector3 Fog;
            public float FogOpacity;
            // The ray's forward component (depth over distance) and the distance to the top of the air.
            public float Along;
            public float Exit;
        }

        // Each band's grid points, owned by that band: no locks between the band workers.
        readonly Acrux.Render3D.BandSlots<GridPoint[]> _grids = new Acrux.Render3D.BandSlots<GridPoint[]>();

        GridPoint[] GridFor(int firstRow, int endRow, int length)
        {
            int band = _grids.IndexOf(firstRow, endRow);
            GridPoint[] grid = band < 0 ? null : _grids[band];
            if (grid == null || grid.Length < length)
            {
                grid = new GridPoint[length];
                if (band >= 0)
                    _grids[band] = grid;
            }
            else
            {
                Array.Clear(grid, 0, length);
            }
            return grid;
        }

        public void Draw(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow)
        {
            AtmosphereParameters air = Atmosphere;
            if (depthBuffer == null || air.TopRadius <= air.GroundRadius)
                return;
            int width = canvas.Width;
            int start = Math.Max(0, firstRow);
            int last = Math.Min(canvas.Height, endRow);
            if (start >= last || width <= 0)
                return;
            bool fog = _fogInverseDistance > 0d;
            int columns = ((width - 1) >> GRID_SHIFT) + 2;
            int firstGridRow = start >> GRID_SHIFT;
            int gridRows = ((last - 1) >> GRID_SHIFT) - firstGridRow + 2;
            GridPoint[] grid = GridFor(firstRow, endRow, columns * gridRows);
            for (int y = start; y < last; y++)
            {
                int rowIndex = ((y >> GRID_SHIFT) - firstGridRow) * columns;
                float ty = ((y & (GRID - 1)) + 0.5f) * (1f / GRID);
                int offset = y * width;
                // One grid cell (GRID pixels) at a time: its four points are evaluated at most once per kind
                // (sky or fog) here instead of being checked at every pixel; the pixels then only blend.
                for (int cellX = 0; cellX < width; cellX += GRID)
                {
                    int cellEnd = Math.Min(width, cellX + GRID);
                    int i00 = rowIndex + (cellX >> GRID_SHIFT);
                    int i10 = i00 + 1;
                    int i01 = i00 + columns;
                    int i11 = i01 + 1;
                    bool skyReady = false;
                    bool fogReady = false;
                    for (int x = cellX; x < cellEnd; x++)
                    {
                        float depth = depthBuffer[offset + x];
                        bool drawn = !float.IsNegativeInfinity(depth);
                        if (drawn && !fog)
                            continue;
                        if (drawn ? !fogReady : !skyReady)
                        {
                            byte flag = drawn ? POINT_FOG : POINT_SKY;
                            Ensure(grid, i00, flag, columns, firstGridRow, ref air);
                            Ensure(grid, i10, flag, columns, firstGridRow, ref air);
                            Ensure(grid, i01, flag, columns, firstGridRow, ref air);
                            Ensure(grid, i11, flag, columns, firstGridRow, ref air);
                            if (drawn)
                                fogReady = true;
                            else
                                skyReady = true;
                        }
                        float tx = ((x & (GRID - 1)) + 0.5f) * (1f / GRID);
                        float w00 = (1f - tx) * (1f - ty);
                        float w10 = tx * (1f - ty);
                        float w01 = (1f - tx) * ty;
                        float w11 = tx * ty;
                        if (drawn)
                            ApplyFog(canvas, x, y, depth, ref grid[i00], ref grid[i10], ref grid[i01], ref grid[i11],
                                w00, w10, w01, w11);
                        else
                            BlendSky(canvas, x, y, ref grid[i00], ref grid[i10], ref grid[i01], ref grid[i11],
                                w00, w10, w01, w11);
                    }
                }
            }
        }

        void Ensure(GridPoint[] grid, int index, byte flag, int columns, int firstGridRow, ref AtmosphereParameters air)
        {
            if ((grid[index].Flags & flag) != 0)
                return;
            grid[index].Flags |= flag;
            int column = index % columns;
            int row = index / columns + firstGridRow;
            var screen = new Vector2(column * GRID, row * GRID);
            SceneCamera camera = Camera;
            Vector3D ray = camera.GetRay(screen);
            Vector3D origin = camera.Position;
            double originRadius2 = origin.LengthSquared();
            double top2 = air.TopRadius * air.TopRadius;
            double b = Vector3D.Dot(origin, ray);
            double discriminant = b * b - (originRadius2 - top2);
            if (flag == POINT_SKY)
            {
                Vector3 premultiplied;
                float opacity;
                SkyAt(ref air, origin, ray, b, discriminant, air.TopRadius - air.GroundRadius,
                    out premultiplied, out opacity);
                grid[index].Sky = premultiplied;
                grid[index].SkyOpacity = opacity;
                return;
            }

            grid[index].Along = (float)Vector3D.Dot(ray, camera.Forward);
            // Past the top of the atmosphere (another planet, seen through the air) there is no more air.
            grid[index].Exit = (float)(-b + Math.Sqrt(Math.Max(0d, discriminant)));
            double angle = Math.Atan2(Vector3D.Dot(ray, _fogNorth), Vector3D.Dot(ray, _fogEast));
            double slot = angle / MathHelper.TwoPi * FOG_DIRECTIONS;
            if (slot < 0d)
                slot += FOG_DIRECTIONS;
            int first = (int)slot % FOG_DIRECTIONS;
            int second = (first + 1) % FOG_DIRECTIONS;
            float blend = (float)(slot - Math.Floor(slot));
            Color a = _fogColors[first];
            Color c = _fogColors[second];
            grid[index].Fog = new Vector3(a.R + (c.R - a.R) * blend, a.G + (c.G - a.G) * blend,
                a.B + (c.B - a.B) * blend);
            grid[index].FogOpacity = _fogOpacities[first] + (_fogOpacities[second] - _fogOpacities[first]) * blend;
        }

        // The sky through one ray: premultiplied tint (0..255) and opacity, zero when the ray misses the air.
        static void SkyAt(ref AtmosphereParameters air, Vector3D origin, Vector3D ray, double b, double discriminant,
            double shell, out Vector3 premultiplied, out float opacity)
        {
            premultiplied = Vector3.Zero;
            opacity = 0f;
            // The ray's span inside the outer sphere. No other sphere bounds a sky ray: mountains are not a
            // sphere, and a ray that missed every drawn surface crossed the air all the way out. One that would
            // dip into the planet outside the terrain mesh marches through dense air and comes out properly
            // opaque.
            if (discriminant <= 0d)
                return;
            double root = Math.Sqrt(discriminant);
            double enter = Math.Max(0d, -b - root);
            double exit = -b + root;
            if (exit <= enter)
                return;

            Vector3 transmittance;
            Vector3 scattered;
            March(ref air, origin, ray, enter, exit, shell, out transmittance, out scattered);
            Color tint = Composite(ref air, transmittance, scattered, ray, out opacity);
            premultiplied = new Vector3(tint.R, tint.G, tint.B) * opacity;
        }

        void BlendSky(PixelCanvas canvas, int x, int y, ref GridPoint p00, ref GridPoint p10, ref GridPoint p01,
            ref GridPoint p11, float w00, float w10, float w01, float w11)
        {
            float opacity = p00.SkyOpacity * w00 + p10.SkyOpacity * w10 + p01.SkyOpacity * w01 + p11.SkyOpacity * w11;
            if (opacity <= 1f / 255f)
                return;
            Vector3 light = (p00.Sky * w00 + p10.Sky * w10 + p01.Sky * w01 + p11.Sky * w11) * (1f / opacity);
            canvas.BlendPixel(x, y, new Color(
                (byte)MathHelper.Clamp(light.X, 0f, 255f),
                (byte)MathHelper.Clamp(light.Y, 0f, 255f),
                (byte)MathHelper.Clamp(light.Z, 0f, 255f)), opacity);
        }

        // A drawn pixel fades toward the horizon sky in its direction, by the air between it and the camera.
        void ApplyFog(PixelCanvas canvas, int x, int y, float depth, ref GridPoint p00, ref GridPoint p10,
            ref GridPoint p01, ref GridPoint p11, float w00, float w10, float w01, float w11)
        {
            float along = p00.Along * w00 + p10.Along * w10 + p01.Along * w01 + p11.Along * w11;
            if (along <= 1e-6f)
                return;
            float distance = -depth / along;
            float exit = p00.Exit * w00 + p10.Exit * w10 + p01.Exit * w01 + p11.Exit * w11;
            float air = Math.Min(distance, exit);
            if (air <= 0f)
                return;
            float opacity = p00.FogOpacity * w00 + p10.FogOpacity * w10 + p01.FogOpacity * w01 + p11.FogOpacity * w11;
            float amount = (1f - (float)Math.Exp(-air * _fogInverseDistance)) * opacity;
            if (amount <= 1f / 255f)
                return;
            Vector3 color = p00.Fog * w00 + p10.Fog * w10 + p01.Fog * w01 + p11.Fog * w11;
            canvas.BlendPixel(x, y, new Color(
                (byte)MathHelper.Clamp(color.X + 0.5f, 0f, 255f),
                (byte)MathHelper.Clamp(color.Y + 0.5f, 0f, 255f),
                (byte)MathHelper.Clamp(color.Z + 0.5f, 0f, 255f)), amount);
        }

        // Air hides what is behind it by its mean extinction; scattered light (scaled by Intensity, as the game
        // exposes its sky) brighter than that allows hides more.
        static Color Composite(ref AtmosphereParameters air, Vector3 transmittance, Vector3 scattered, Vector3D ray,
            out float opacity)
        {
            // Rayleigh phase: a little brighter toward and away from the sun.
            float cosine = Vector3.Dot((Vector3)ray, air.SunDirection);
            float phase = 0.75f * (1f + cosine * cosine);
            Vector3 light = scattered * (phase * Exposure(air.Intensity));
            float brightest = Math.Max(light.X, Math.Max(light.Y, light.Z));
            opacity = MathHelper.Clamp(
                Math.Max(1f - (transmittance.X + transmittance.Y + transmittance.Z) / 3f, brightest), 0f, 1f);
            if (opacity <= 1e-4f)
                return Color.Black;
            Vector3 tint = light * (255f / opacity);
            return new Color(
                (byte)MathHelper.Clamp(tint.X, 0f, 255f),
                (byte)MathHelper.Clamp(tint.Y, 0f, 255f),
                (byte)MathHelper.Clamp(tint.Z, 0f, 255f));
        }

        // The game multiplies its scattering by Intensity in HDR before tone mapping; on an 8-bit LCD a
        // square root keeps EarthLike's 6 bright without washing a default-intensity sky out.
        static float Exposure(float intensity)
        {
            return (float)Math.Sqrt(Math.Max(0.05f, intensity > 0f ? intensity : 1f));
        }

        // Extinction of the whole segment, and the sunlight it scatters toward the camera, both per channel.
        static void March(ref AtmosphereParameters air, Vector3D origin, Vector3D ray, double enter, double exit,
            double shell, out Vector3 transmittance, out Vector3 scattered)
        {
            double step = (exit - enter) / SAMPLES;
            float stepShells = (float)(step / shell);
            Vector3 depth = Vector3.Zero;
            scattered = Vector3.Zero;
            for (int i = 0; i < SAMPLES; i++)
            {
                Vector3D point = origin + ray * (enter + (i + 0.5d) * step);
                double radius = point.Length();
                double altitude = Math.Max(0d, (radius - air.GroundRadius) / shell);
                float density = (float)Math.Exp(-altitude / air.ScaleHeight);
                float segment = density * stepShells;
                // Optical depth from the camera to the middle of this sample.
                Vector3 toSample = depth + air.Scattering * (segment * 0.5f);
                depth += air.Scattering * segment;

                Vector3 up = (Vector3)(point / Math.Max(1e-9, radius));
                float sunLight = SunVisibility(up, air.SunDirection, radius, air.GroundRadius);
                if (sunLight <= 0f)
                    continue;
                // The air between this sample and the sun: thicker for a low sun (airmass), thinner up high.
                float mu = Vector3.Dot(up, air.SunDirection);
                float airmass = 1f / (Math.Max(mu, 0f) * 0.95f + 0.05f);
                Vector3 toSun = air.Scattering * (density * (float)air.ScaleHeight * airmass);
                Vector3 path = (toSample + toSun) * EXTINCTION;
                Vector3 reaching = air.SunColor * new Vector3(
                    (float)Math.Exp(-path.X), (float)Math.Exp(-path.Y), (float)Math.Exp(-path.Z));
                scattered += reaching * air.Scattering * (segment * EXTINCTION * sunLight);
            }
            transmittance = new Vector3(
                (float)Math.Exp(-depth.X * EXTINCTION),
                (float)Math.Exp(-depth.Y * EXTINCTION),
                (float)Math.Exp(-depth.Z * EXTINCTION));
        }

        // No ray marching: one closed-form optical depth per pixel. Past the limb a tangent ray crosses sqrt(2 pi R
        // H) of air at its lowest height.
        public static void DrawGlobe(
            PixelCanvas canvas,
            int centerX,
            int centerY,
            double radiusPixels,
            double planetRadiusMeters,
            AtmosphereParameters air,
            Vector3 view,
            Vector3 right,
            Vector3 up,
            float[] depthBuffer,
            float nearestDepth,
            float discStrength,
            int firstRow,
            int endRow)
        {
            double shell = air.TopRadius - air.GroundRadius;
            if (shell <= 0d || radiusPixels <= 0d || planetRadiusMeters <= 0d)
                return;
            // From space the glow hugs the limb: a thin scale height of its own, not the in-air sky's (which
            // would spread the halo across most of the shell, far wider than the planet itself).
            double scaleHeight = shell * GLOBE_SCALE_HEIGHT;
            double pixelsPerMeter = radiusPixels / planetRadiusMeters;
            double groundPixels = air.GroundRadius * pixelsPerMeter;
            if (groundPixels < 0.5d)
                return;
            // The glow is gone well before the top of the shell.
            double outerPixels = Math.Min(air.TopRadius, air.GroundRadius + 5d * scaleHeight) * pixelsPerMeter;
            float column = (float)(scaleHeight / shell) * EXTINCTION;
            float grazing = (float)(Math.Sqrt(MathHelper.TwoPi * air.GroundRadius * scaleHeight) / shell) * EXTINCTION;
            float exposure = Exposure(air.Intensity);
            var sun = new Vector3(
                Vector3.Dot(air.SunDirection, right),
                Vector3.Dot(air.SunDirection, up),
                Vector3.Dot(air.SunDirection, view));

            int top = (int)Math.Max(Math.Max(0, firstRow), Math.Floor(centerY - outerPixels));
            int bottom = (int)Math.Min(Math.Min(canvas.Height, endRow) - 1, Math.Ceiling(centerY + outerPixels));
            int left = (int)Math.Max(0, Math.Floor(centerX - outerPixels));
            int rightEdge = (int)Math.Min(canvas.Width - 1, Math.Ceiling(centerX + outerPixels));
            double outer2 = outerPixels / groundPixels;
            outer2 *= outer2;
            int width = canvas.Width;
            for (int y = top; y <= bottom; y++)
            {
                float dy = (float)((centerY - (y + 0.5d)) / groundPixels);
                for (int x = left; x <= rightEdge; x++)
                {
                    float dx = (float)((x + 0.5d - centerX) / groundPixels);
                    float d2 = dx * dx + dy * dy;
                    if (d2 > outer2)
                        continue;
                    if (depthBuffer != null && depthBuffer[y * width + x] > nearestDepth)
                        continue;

                    float d = (float)Math.Sqrt(d2);
                    Vector3 normal;
                    float density;
                    float viewPath;
                    float strength;
                    if (d < 1f)
                    {
                        float facing = (float)Math.Sqrt(1f - d2);
                        normal = new Vector3(dx, dy, facing);
                        density = 1f;
                        viewPath = column / (facing * 0.95f + 0.05f);
                        strength = discStrength;
                    }
                    else
                    {
                        normal = new Vector3(dx / d, dy / d, 0f);
                        density = (float)Math.Exp(-(d - 1f) * air.GroundRadius / scaleHeight);
                        viewPath = grazing * density;
                        strength = 1f;
                    }
                    if (strength <= 0f)
                        continue;

                    // The air this pixel looks through: on the tangent ray at its height past the limb, or just
                    // above the ground over the disc; in planet radii, in the view basis.
                    Vector3 point = d >= 1f
                        ? new Vector3(dx, dy, 0f)
                        : normal * (float)(1d + scaleHeight / air.GroundRadius);
                    // The planet's shadow is a cylinder behind it, away from the sun: air inside it gets no
                    // sunlight, wherever on the disc or the ring it lies. Its edge is soft.
                    float lit = 1f;
                    float along = Vector3.Dot(point, sun);
                    if (along < 0f)
                    {
                        float axis = (point - sun * along).Length();
                        lit = MathHelper.Clamp((axis - (1f - SHADOW_EDGE)) / (2f * SHADOW_EDGE), 0f, 1f);
                        lit = lit * lit * (3f - 2f * lit);
                    }
                    // Sunlight reaching that air crossed more of it the lower the sun stands there: grazing
                    // light past the limb, and the terminator, come out reddened.
                    float mu = Vector3.Dot(point / Math.Max(1e-6f, point.Length()), sun);
                    float sunPath = column * density / (Math.Max(mu, 0f) * 0.95f + 0.05f);
                    Vector3 b = air.Scattering;
                    var transmittance = new Vector3(
                        (float)Math.Exp(-b.X * viewPath),
                        (float)Math.Exp(-b.Y * viewPath),
                        (float)Math.Exp(-b.Z * viewPath));
                    var reaching = air.SunColor * new Vector3(
                        (float)Math.Exp(-b.X * sunPath),
                        (float)Math.Exp(-b.Y * sunPath),
                        (float)Math.Exp(-b.Z * sunPath));
                    Vector3 light = (Vector3.One - transmittance) * reaching * (lit * exposure * strength);
                    float brightest = Math.Max(light.X, Math.Max(light.Y, light.Z));
                    // The air hides what is behind it whether lit or not: in the shadow it is a dark tint, so the
                    // atmosphere stays a full circle and only its light follows the sun.
                    float hidden = (1f - (transmittance.X + transmittance.Y + transmittance.Z) / 3f) * strength;
                    // Off the disc only the sky is behind, and the glow may be as opaque as it is bright. Over
                    // the ground the air hides only its extinction: the map must stay readable under the haze.
                    float opacity = MathHelper.Clamp(d < 1f ? hidden : Math.Max(hidden, brightest), 0f, 1f);
                    if (opacity <= 1f / 255f)
                        continue;
                    Vector3 tint = light * (1f / opacity);
                    // Too bright for its opacity: keep the hue, lose some brightness.
                    float peak = Math.Max(tint.X, Math.Max(tint.Y, tint.Z));
                    if (peak > 1f)
                        tint /= peak;
                    tint *= 255f;
                    canvas.BlendPixel(x, y, new Color(
                        (byte)MathHelper.Clamp(tint.X, 0f, 255f),
                        (byte)MathHelper.Clamp(tint.Y, 0f, 255f),
                        (byte)MathHelper.Clamp(tint.Z, 0f, 255f)), opacity);
                }
            }
        }

        // 1 where the sample sees the sun, fading to 0 through a short twilight below its own horizon.
        static float SunVisibility(Vector3 up, Vector3 sun, double radius, double groundRadius)
        {
            double ratio = Math.Min(1d, groundRadius / Math.Max(groundRadius, radius));
            float horizon = -(float)Math.Sqrt(Math.Max(0d, 1d - ratio * ratio));
            float mu = Vector3.Dot(up, sun);
            return MathHelper.Clamp((mu - horizon) / TWILIGHT + 1f, 0f, 1f);
        }
    }
}
