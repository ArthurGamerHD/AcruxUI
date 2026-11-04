using System;
using System.Collections.Generic;
using System.IO;
using Adk.Image;
using Adk.Image.Dds;
using Sandbox.ModAPI;
using VRageMath;

namespace Acrux.Cartography
{
    /// <summary>
    /// A downscale (at most <see cref="PlanetMaterialTextures.SIDE"/> texels) of one voxel color texture, with
    /// its box-filtered mip chain, point-sampled: one texel of one mip, no filtering. Texels are packed as
    /// 0x00BBGGRR so a lookup is a single read.
    /// </summary>
    internal sealed class MaterialTexture
    {
        readonly int[][] _levels;
        readonly int[] _sides;

        public MaterialTexture(RawRgbaBitmap bitmap)
        {
            int side = Math.Min(bitmap.Width, bitmap.Height);
            var rgb = new byte[side * side * 3];
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    int source = y * bitmap.Stride + x * 4;
                    int target = (y * side + x) * 3;
                    rgb[target] = bitmap.Pixels[source];
                    rgb[target + 1] = bitmap.Pixels[source + 1];
                    rgb[target + 2] = bitmap.Pixels[source + 2];
                }
            }

            var levels = new List<int[]> { Pack(rgb, side) };
            var sides = new List<int> { side };
            while (side > 1)
            {
                int next = side / 2;
                var reduced = new byte[next * next * 3];
                for (int y = 0; y < next; y++)
                {
                    for (int x = 0; x < next; x++)
                    {
                        int a = ((2 * y) * side + 2 * x) * 3;
                        int b = a + 3;
                        int c = a + side * 3;
                        int d = c + 3;
                        int target = (y * next + x) * 3;
                        for (int channel = 0; channel < 3; channel++)
                            reduced[target + channel] = (byte)((rgb[a + channel] + rgb[b + channel] +
                                                                rgb[c + channel] + rgb[d + channel] + 2) / 4);
                    }
                }
                levels.Add(Pack(reduced, next));
                sides.Add(next);
                rgb = reduced;
                side = next;
            }
            _levels = levels.ToArray();
            _sides = sides.ToArray();
        }

        static int[] Pack(byte[] rgb, int side)
        {
            var packed = new int[side * side];
            for (int i = 0; i < packed.Length; i++)
                packed[i] = rgb[i * 3] | (rgb[i * 3 + 1] << 8) | (rgb[i * 3 + 2] << 16);
            return packed;
        }

        public int Side
        {
            get { return _sides[0]; }
        }

        public int LevelCount
        {
            get { return _levels.Length; }
        }

        public int Texel(float u, float v, int level)
        {
            int[] data = _levels[level];
            int side = _sides[level];
            int x = (int)(u * side);
            int y = (int)(v * side);
            if (x >= side) x = side - 1; else if (x < 0) x = 0;
            if (y >= side) y = side - 1; else if (y < 0) y = 0;
            return data[y * side + x];
        }
    }

    /// <summary>
    /// How one voxel material looks, tiled as the game's triplanar shader tiles it (TriplanarSampling.hlsli,
    /// full quality): a near scale that grows in steps with camera distance, cross-faded over the end of each
    /// step; the Far1 and Far2 sets at their fixed scales from their switch distances; and past Far3Distance
    /// the far color alone. Textures are point-sampled from the mip that fits the pixel's footprint (no
    /// filtering: up close a coarse mip just shows larger texels). Until the textures load, or where a set
    /// failed to load, the far color stands in.
    /// </summary>
    internal sealed class MaterialAppearance
    {
        // TILING_MULTIPLIER of the full-quality shader: initial scale, initial distance, scale step, distance step.
        const float NEAR_SCALE_MULTIPLIER = 2f;
        const float NEAR_DISTANCE_MULTIPLIER = 2f;
        const float SCALE_STEP_MULTIPLIER = 2f;
        const float DISTANCE_STEP_MULTIPLIER = 4f;
        // NEARFAR_TRANSITION_LENGTH: the share at the end of each near step over which the next scale fades in.
        const double NEARFAR_TRANSITION_LENGTH = 0.75d;
        // GetNearestDistanceAndScaleLoop's iteration cap.
        const int MAXIMUM_NEAR_STEPS = 16;
        // The texture fades into the far color between these fractions of Far3Distance (the game switches
        // hard; at that range the texture's mip is close to its far color anyway).
        const double FAR_FADE_START = 0.85d;

        readonly MaterialTextureSnapshot _source;
        // Written once by the loader, read by paint workers.
        internal volatile MaterialTexture[] Sets;

        public MaterialAppearance(MaterialTextureSnapshot source)
        {
            _source = source;
        }

        public MaterialTextureSnapshot Source
        {
            get { return _source; }
        }

        public Color Sample(Vector3D position, Vector3 normal, double distance, double footprintMeters, Color far)
        {
            MaterialTexture[] sets = Sets;
            if (sets == null)
                return far;
            MaterialTextureSnapshot s = _source;

            float farWeight = 0f;
            if (s.Far3Distance > 0f)
            {
                if (distance >= s.Far3Distance)
                    return far;
                double start = s.Far3Distance * FAR_FADE_START;
                farWeight = (float)SmoothStep((distance - start) / (s.Far3Distance - start));
            }

            // GetNearestDistanceAndScaleLoop: the near step the camera distance falls in, and the next one.
            double nearDistance = 0d;
            double nearScale = s.InitialScale * NEAR_SCALE_MULTIPLIER;
            double farDistance = s.InitialDistance * NEAR_DISTANCE_MULTIPLIER;
            double farScale = s.ScaleMultiplier * SCALE_STEP_MULTIPLIER;
            double distanceStep = s.DistanceMultiplier * DISTANCE_STEP_MULTIPLIER;
            double scaleStep = s.ScaleMultiplier * SCALE_STEP_MULTIPLIER;
            for (int i = 0; farDistance < distance && i <= MAXIMUM_NEAR_STEPS; i++)
            {
                nearDistance = farDistance;
                nearScale = farScale;
                farDistance *= distanceStep;
                farScale *= scaleStep;
            }
            double span = farDistance - nearDistance;
            double scaleWeight = span > 1e-9d
                ? Saturate(((distance - nearDistance) / span - (1d - NEARFAR_TRANSITION_LENGTH)) /
                           NEARFAR_TRANSITION_LENGTH)
                : 0d;

            // GetTriplanarWeights: only components past 0.55 count, so most ground takes one projection.
            float wx = Math.Max(0f, Math.Abs(normal.X) - 0.55f);
            float wy = Math.Max(0f, Math.Abs(normal.Y) - 0.55f);
            float wz = Math.Max(0f, Math.Abs(normal.Z) - 0.55f);
            wx *= wx; wx *= wx;
            wy *= wy; wy *= wy;
            wz *= wz; wz *= wz;
            float total = wx + wy + wz;
            if (!(total > 1e-12f))
            {
                wy = 1f;
                total = 1f;
            }
            float inverseTotal = 1f / total;
            wx *= inverseTotal;
            wy *= inverseTotal;
            wz *= inverseTotal;

            Vector3 farColor = new Vector3(far.R, far.G, far.B);
            Vector3 color;
            if (scaleWeight <= 0.005d)
                color = SampleDistance(sets, s, nearScale, nearDistance, position, wx, wy, wz, footprintMeters, farColor);
            else if (scaleWeight >= 0.995d)
                color = SampleDistance(sets, s, farScale, farDistance, position, wx, wy, wz, footprintMeters, farColor);
            else
            {
                Vector3 near = SampleDistance(sets, s, nearScale, nearDistance, position, wx, wy, wz,
                    footprintMeters, farColor);
                Vector3 next = SampleDistance(sets, s, farScale, farDistance, position, wx, wy, wz,
                    footprintMeters, farColor);
                color = near + (next - near) * (float)scaleWeight;
            }

            if (farWeight > 0f)
                color += (farColor - color) * farWeight;
            return new Color(
                (byte)MathHelper.Clamp(color.X + 0.5f, 0f, 255f),
                (byte)MathHelper.Clamp(color.Y + 0.5f, 0f, 255f),
                (byte)MathHelper.Clamp(color.Z + 0.5f, 0f, 255f),
                255);
        }

        // SampleTriplanarDistance: the step's own texture set and scale, chosen by the step's distance.
        static Vector3 SampleDistance(MaterialTexture[] sets, MaterialTextureSnapshot s, double scale,
            double stepDistance, Vector3D position, float wx, float wy, float wz, double footprintMeters,
            Vector3 farColor)
        {
            int set = 0;
            if (s.Far1Distance > 0f && s.Far1Scale > 0f && stepDistance >= s.Far1Distance)
            {
                scale = s.Far1Scale;
                set = 1;
            }
            if (s.Far2Distance > 0f && s.Far2Scale > 0f && stepDistance >= s.Far2Distance)
            {
                scale = s.Far2Scale;
                set = 2;
            }
            if (s.Far3Distance > 0f && stepDistance >= s.Far3Distance)
                return farColor;
            // A missing set borrows the nearest loaded one rather than dropping to the flat color.
            MaterialTexture texture = sets[set] ?? sets[set == 0 ? 1 : set - 1] ?? sets[0];
            if (texture == null || !(scale > 1e-3d))
                return farColor;

            // The mip whose texels are closest to the pixel's footprint, rounded to the nearest level.
            double texelMeters = scale / texture.Side;
            double ratio = footprintMeters / texelMeters;
            int level = 0;
            int lastLevel = texture.LevelCount - 1;
            while (ratio >= 1.41421356d && level < lastLevel)
            {
                ratio *= 0.5d;
                level++;
            }

            double inverse = 1d / scale;
            // Wrap the tile coordinate in double before narrowing, or far-off planets lose the fraction.
            float px = (float)Fraction(position.X * inverse);
            float py = (float)Fraction(position.Y * inverse);
            float pz = (float)Fraction(position.Z * inverse);
            Vector3 sum = Vector3.Zero;
            if (wx > 0f)
                sum += Unpack(texture.Texel(pz, py, level)) * wx;
            if (wy > 0f)
                sum += Unpack(texture.Texel(px, pz, level)) * wy;
            if (wz > 0f)
                sum += Unpack(texture.Texel(px, py, level)) * wz;
            return sum;
        }

        static Vector3 Unpack(int texel)
        {
            return new Vector3(texel & 0xFF, (texel >> 8) & 0xFF, (texel >> 16) & 0xFF);
        }

        static double Saturate(double value)
        {
            return value < 0d ? 0d : value > 1d ? 1d : value;
        }

        static double SmoothStep(double t)
        {
            t = Saturate(t);
            return t * t * (3d - 2d * t);
        }

        static double Fraction(double value)
        {
            return value - Math.Floor(value);
        }
    }

    /// <summary>
    /// Loads and downscales voxel color textures on one low-priority background task, shared by every planet:
    /// each texture decodes only its stored mip of at most <see cref="SIDE"/> texels (the game's are 1-2k).
    /// </summary>
    internal static class PlanetMaterialTextures
    {
        public const int SIDE = 256;

        static readonly object Gate = new object();
        static readonly Dictionary<string, MaterialAppearance> Appearances =
            new Dictionary<string, MaterialAppearance>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<MaterialAppearance> Pending = new Queue<MaterialAppearance>();
        static volatile bool _loading;
        static int _generation;

        public static bool IsLoading
        {
            get { return _loading; }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _generation++;
                Appearances.Clear();
                Pending.Clear();
                _loading = false;
            }
        }

        // Queued for loading on first request; null without textures.
        public static MaterialAppearance Get(MaterialTextureSnapshot source)
        {
            if (source == null)
                return null;
            string key = BuildKey(source);
            lock (Gate)
            {
                MaterialAppearance appearance;
                if (Appearances.TryGetValue(key, out appearance))
                    return appearance;
                appearance = new MaterialAppearance(source);
                Appearances[key] = appearance;
                Pending.Enqueue(appearance);
                if (!_loading)
                {
                    _loading = true;
                    int generation = _generation;
                    try
                    {
                        MyAPIGateway.Parallel.StartBackground(() => Drain(generation));
                    }
                    catch (Exception)
                    {
                        _loading = false;
                    }
                }
                return appearance;
            }
        }

        static string BuildKey(MaterialTextureSnapshot source)
        {
            return (source.IsBaseGame ? "game|" : "mod|" + source.ModItem.PublishedFileId + "|" + source.ModItem.Name + "|") +
                   source.Subtype;
        }

        static void Drain(int generation)
        {
            while (true)
            {
                MaterialAppearance appearance;
                lock (Gate)
                {
                    if (generation != _generation || Pending.Count == 0)
                    {
                        if (generation == _generation)
                            _loading = false;
                        return;
                    }
                    appearance = Pending.Dequeue();
                }

                var sets = new MaterialTexture[3];
                bool any = false;
                for (int i = 0; i < sets.Length; i++)
                {
                    sets[i] = TryLoad(appearance.Source, appearance.Source.Paths[i]);
                    any |= sets[i] != null;
                }
                if (any)
                    appearance.Sets = sets;
            }
        }

        // The defining mod first, then game content: the same precedence as the far-color texture fallbacks.
        static MaterialTexture TryLoad(MaterialTextureSnapshot source, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var utilities = MyAPIGateway.Utilities;
            if (utilities == null)
                return null;
            try
            {
                if (!source.IsBaseGame && source.HasModItem && utilities.FileExistsInModLocation(path, source.ModItem))
                {
                    using (BinaryReader reader = utilities.ReadBinaryFileInModLocation(path, source.ModItem))
                    {
                        MaterialTexture texture = Decode(reader);
                        if (texture != null)
                            return texture;
                    }
                }
                if (utilities.FileExistsInGameContent(path))
                {
                    using (BinaryReader reader = utilities.ReadBinaryFileInGameContent(path))
                        return Decode(reader);
                }
            }
            catch (Exception)
            {
                // Optional: the material keeps its far color.
            }
            return null;
        }

        static MaterialTexture Decode(BinaryReader reader)
        {
            if (reader == null)
                return null;
            RawRgbaBitmap bitmap;
            string failure;
            if (!DdsDecoder.TryDecodeMipAtMost(reader.BaseStream, SIDE, out bitmap, out failure) ||
                bitmap.Width < 1 || bitmap.Height < 1)
                return null;
            return new MaterialTexture(bitmap);
        }
    }
}
