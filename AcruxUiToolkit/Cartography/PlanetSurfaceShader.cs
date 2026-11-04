using System;
using System.Collections.Generic;
using Adk.Image.Png;
using Sandbox.ModAPI;
using VRageMath;

namespace Acrux.Cartography
{
    /// <summary>
    /// Per-pixel planet surface color for the 3D surface view, evaluated the way the voxel generator assigns
    /// surface material (MyPlanetMaterialProvider): the material map value at the point, blended between
    /// neighbouring texels by the generator's tileset, picks a direct material or a group whose rules test
    /// the patch height, latitude, longitude and slope; the material's far color is the pixel's color.
    /// The color cubemap only holds that answer at each texel centre, so up close this shader replaces it.
    /// Immutable once built on the cartography worker.
    /// </summary>
    internal sealed class PlanetSurfaceShader
    {
        readonly CartographyLayer _layer;
        readonly byte[][] _materials;
        readonly int _materialResolution;
        readonly PlanetBlendTileset _blend;
        // Each distinct surface material once: its far color and, when it has textures, how it looks up close.
        sealed class Slot
        {
            public Color Far;
            public MaterialAppearance Appearance;
        }

        readonly List<Slot> _slots = new List<Slot>();
        readonly Dictionary<string, int> _slotByMaterial = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly int[] _direct = new int[256];
        readonly MaterialRuleSnapshot[][] _rules = new MaterialRuleSnapshot[256][];
        readonly int[][] _ruleSlots = new int[256][];
        int _default;
        readonly float _minimumHeight;
        readonly float _heightRange;

        PlanetSurfaceShader(CartographyLayer layer, byte[][] materials, int materialResolution,
            PlanetBlendTileset blend, PlanetHeightCubemap heights)
        {
            _layer = layer;
            _materials = materials;
            _materialResolution = materialResolution;
            _blend = blend;
            for (int i = 0; i < _direct.Length; i++)
                _direct[i] = -1;
            _minimumHeight = heights.MinimumNormalizedHeight;
            _heightRange = heights.MaximumNormalizedHeight - heights.MinimumNormalizedHeight;
        }

        public bool HasWater { get; private set; }

        public float WaterLevel { get; private set; }

        // Null when per-pixel evaluation would not differ from the cubemap (Materials and Biomes are exact per
        // texel).
        public static PlanetSurfaceShader Create(
            PlanetMapSource source,
            PlanetDefinitionSnapshot planet,
            FarColorCatalogSnapshot farColors,
            PlanetWaterSnapshot water,
            CartographyLayer layer,
            PlanetHeightCubemap heights)
        {
            if (source == null || planet == null || heights == null)
                return null;

            PlanetSurfaceShader shader;
            if (layer == CartographyLayer.Satellite)
            {
                byte[][] materials = source.GetMaterialFaces();
                if (materials == null || farColors == null)
                    return null;
                shader = new PlanetSurfaceShader(layer, materials, source.MaterialResolution,
                    PlanetContentTextures.GetBlendTileset(planet.MaterialBlendTexture, planet.MaterialBlendCellSize),
                    heights);
                shader._default = shader.SlotOf(farColors, planet.DefaultSurfaceMaterial);
                for (int value = 0; value < 256; value++)
                {
                    string direct = planet.DirectSurfaceMaterials[value];
                    if (!string.IsNullOrWhiteSpace(direct))
                    {
                        shader._direct[value] = shader.SlotOf(farColors, direct);
                        continue;
                    }

                    MaterialRuleSnapshot[] rules = planet.MaterialGroups[value];
                    if (rules == null || rules.Length == 0)
                        continue;
                    var slots = new int[rules.Length];
                    for (int i = 0; i < rules.Length; i++)
                        slots[i] = shader.SlotOf(farColors, rules[i] == null ? null : rules[i].SurfaceMaterial);
                    shader._rules[value] = rules;
                    shader._ruleSlots[value] = slots;
                }
            }
            else if (layer == CartographyLayer.Terrain)
            {
                shader = new PlanetSurfaceShader(layer, null, 0, null, heights);
            }
            else
            {
                return null;
            }

            if (water != null)
            {
                shader.HasWater = true;
                shader.WaterLevel = water.HeightmapUnit / (float)ushort.MaxValue;
            }
            return shader;
        }

        // Shader construction only (cartography worker). Texture loading is queued, never awaited.
        int SlotOf(FarColorCatalogSnapshot farColors, string material)
        {
            string key = material ?? string.Empty;
            int index;
            if (_slotByMaterial.TryGetValue(key, out index))
                return index;
            Color color;
            MaterialTextureSnapshot texture;
            var slot = new Slot
            {
                Far = farColors != null && farColors.TryGet(material, out color)
                    ? color
                    : FarColorCatalogSnapshot.MissingColorFallback,
                Appearance = farColors != null && farColors.TryGetTexture(material, out texture)
                    ? PlanetMaterialTextures.Get(texture)
                    : null
            };
            index = _slots.Count;
            _slots.Add(slot);
            _slotByMaterial[key] = index;
            return index;
        }

        public Color Shade(ref PlanetSurfaceSample sample, Vector3 direction, Vector3D position, double distance,
            double footprintMeters)
        {
            return Shade(ref sample, direction, position, distance, footprintMeters, -1);
        }

        // -1 on the height layer, which has no materials.
        public int ResolveSlotIndex(ref PlanetSurfaceSample sample, Vector3 direction)
        {
            return _layer == CartographyLayer.Terrain ? -1 : ResolveSlot(ref sample, direction);
        }

        // A negative slotIndex resolves it here.
        public Color Shade(ref PlanetSurfaceSample sample, Vector3 direction, Vector3D position, double distance,
            double footprintMeters, int slotIndex)
        {
            if (_layer == CartographyLayer.Terrain)
            {
                float value = _heightRange <= 1e-7f
                    ? 0.5f
                    : MathHelper.Clamp((sample.NormalizedHeight - _minimumHeight) / _heightRange, 0f, 1f);
                byte shade = (byte)(value * 255f + 0.5f);
                return new Color(shade, shade, shade, 255);
            }

            Slot slot = _slots[slotIndex >= 0 ? slotIndex : ResolveSlot(ref sample, direction)];
            return slot.Appearance == null
                ? slot.Far
                : slot.Appearance.Sample(position, sample.Normal, distance, footprintMeters, slot.Far);
        }

        int ResolveSlot(ref PlanetSurfaceSample sample, Vector3 direction)
        {
            byte material = SampleMaterial(sample.Face, sample.U, sample.V);
            if (_direct[material] >= 0)
                return _direct[material];
            MaterialRuleSnapshot[] rules = _rules[material];
            if (rules != null)
            {
                float latitude = direction.Y;
                float longitude = PlanetMapSource.GetLongitudeRuleValue(direction);
                int[] slots = _ruleSlots[material];
                for (int i = 0; i < rules.Length; i++)
                {
                    MaterialRuleSnapshot rule = rules[i];
                    if (rule != null && rule.Matches(sample.NormalizedHeight, latitude, longitude, sample.SlopeCosine))
                        return slots[i];
                }
            }
            return _default;
        }

        // Where a texel quad's corners disagree, the tileset picks the corner
        // (MyPlanetMaterialProvider.ComputeMapBlend), so borders meander instead of following the texel grid.
        byte SampleMaterial(PlanetCubeFace face, float u, float v)
        {
            byte[] map = _materials[(int)face];
            int resolution = _materialResolution;
            int last = resolution - 1;
            float x = u * resolution - 0.5f;
            float y = v * resolution - 0.5f;
            if (_blend == null)
            {
                int nx = (int)(u * resolution);
                int ny = (int)(v * resolution);
                if (nx > last) nx = last;
                if (ny > last) ny = last;
                return map[ny * resolution + nx];
            }

            // Truncation, as the game's Vector2I conversion; the fraction below still uses the floor.
            int x0 = (int)x;
            int y0 = (int)y;
            if (x0 < 0) x0 = 0; else if (x0 > last) x0 = last;
            if (y0 < 0) y0 = 0; else if (y0 > last) y0 = last;
            int x1 = Math.Min(last, x0 + 1);
            int y1 = Math.Min(last, y0 + 1);
            byte topLeft = map[y0 * resolution + x0];
            byte topRight = map[y0 * resolution + x1];
            byte bottomLeft = map[y1 * resolution + x0];
            byte bottomRight = map[y1 * resolution + x1];
            if (topLeft == topRight && bottomLeft == bottomRight && bottomLeft == topLeft)
                return topLeft;

            float s = x - (float)Math.Floor(x);
            float t = y - (float)Math.Floor(y);
            if (s >= 1f) s = 0.99999f;
            if (t >= 1f) t = 0.99999f;

            byte v0 = topLeft;
            byte v1 = topRight;
            byte v2 = bottomLeft;
            byte v3 = bottomRight;
            Sort4(ref v0, ref v1, ref v2, ref v3);

            // Ascending distinct values; each one's corner mask picks a tile, and the first tile that does
            // not cover the point leaves the previous value in place.
            byte result = 0;
            byte previous = 0;
            for (int i = 0; i < 4; i++)
            {
                byte value = i == 0 ? v0 : i == 1 ? v1 : i == 2 ? v2 : v3;
                if (i > 0 && value == previous)
                    continue;
                previous = value;
                int corners = (value == topLeft ? 8 : 0) | (value == topRight ? 4 : 0) |
                              (value == bottomLeft ? 2 : 0) | (value == bottomRight ? 1 : 0);
                result = value;
                if (_blend.GetValue(corners, s, t) == 0)
                    break;
            }
            return result;
        }

        // The generator's sorting network, including its no-op third exchange, so ties resolve identically.
        static void Sort4(ref byte v0, ref byte v1, ref byte v2, ref byte v3)
        {
            byte swap;
            if (v0 > v1) { swap = v1; v1 = v0; v0 = swap; }
            if (v2 > v3) { swap = v2; v2 = v3; v3 = swap; }
            if (v1 > v2) { swap = v1; v1 = v2; v2 = swap; }
            if (v0 > v1) { swap = v1; v1 = v0; v0 = swap; }
            if (v2 > v3) { swap = v2; v2 = v3; v3 = swap; }
        }
    }

    /// <summary>
    /// The material blending tileset (MyTileTexture): a 4x4 grid of cells, one per combination of quad corners
    /// that share a material, each marking which part of the quad that material covers.
    /// </summary>
    internal sealed class PlanetBlendTileset
    {
        readonly byte[] _data;
        readonly int _stride;
        readonly int _height;
        readonly int _cellSize;

        public PlanetBlendTileset(byte[] data, int stride, int height, int cellSize)
        {
            _data = data;
            _stride = stride;
            _height = height;
            _cellSize = cellSize;
        }

        public byte GetValue(int corners, float s, float t)
        {
            if (corners > 15)
                return 0;
            int x = (int)(s * _cellSize) + (corners & 3) * _cellSize;
            int y = (int)(t * _cellSize) + (corners >> 2) * _cellSize;
            if (x >= _stride || y >= _height)
                return 0;
            return _data[y * _stride + x];
        }
    }

    /// <summary>An 8-bit height texture with its box-filtered mip chain, shared by every planet that uses it.</summary>
    internal sealed class PlanetDetailTexture
    {
        public readonly byte[][] Levels;
        public readonly int[] Resolutions;

        public PlanetDetailTexture(byte[] data, int resolution)
        {
            var levels = new List<byte[]>();
            var resolutions = new List<int>();
            levels.Add(data);
            resolutions.Add(resolution);
            while (resolution > 1)
            {
                int next = resolution / 2;
                var reduced = new byte[next * next];
                for (int y = 0; y < next; y++)
                {
                    int top = 2 * y * resolution;
                    int bottom = top + resolution;
                    for (int x = 0; x < next; x++)
                    {
                        int sum = data[top + 2 * x] + data[top + 2 * x + 1] +
                                  data[bottom + 2 * x] + data[bottom + 2 * x + 1];
                        reduced[y * next + x] = (byte)((sum + 2) / 4);
                    }
                }
                levels.Add(reduced);
                resolutions.Add(next);
                data = reduced;
                resolution = next;
            }
            Levels = levels.ToArray();
            Resolutions = resolutions.ToArray();
        }
    }

    /// <summary>
    /// The generator's surface detail for one planet (MyPlanetShapeProvider.SurfaceDetailSampler): a tiling
    /// texture of <see cref="ScaleMeters"/> relief, repeating every Size metres of face, added only where the
    /// slope lies in the definition's range and faded across its transition.
    /// </summary>
    internal sealed class PlanetDetailMap
    {
        readonly PlanetDetailTexture _texture;
        readonly float _factor;
        readonly double _texelMeters;
        readonly float _minimum;
        readonly float _maximum;
        readonly float _in;
        readonly float _out;
        readonly float _inReciprocal;
        readonly float _outReciprocal;
        readonly float _middle;

        PlanetDetailMap(PlanetDetailTexture texture, PlanetDefinitionSnapshot planet)
        {
            _texture = texture;
            ScaleMeters = planet.DetailScaleMeters;
            double faceMeters = planet.RadiusMeters * MathHelper.PiOver2;
            _factor = (float)(faceMeters / planet.DetailSizeMeters);
            _texelMeters = planet.DetailSizeMeters / Math.Max(1, texture.Resolutions[0]);
            float minimum = planet.DetailSlopeMinimumDegrees;
            float maximum = planet.DetailSlopeMaximumDegrees;
            float transition = planet.DetailTransitionDegrees;
            _minimum = (float)Math.Cos(MathHelper.ToRadians(maximum));
            _maximum = (float)Math.Cos(MathHelper.ToRadians(minimum));
            _in = (float)Math.Cos(MathHelper.ToRadians(maximum - transition));
            _out = (float)Math.Cos(MathHelper.ToRadians(minimum + transition));
            _inReciprocal = Reciprocal(_in - _minimum);
            _outReciprocal = Reciprocal(_maximum - _out);
            _middle = (float)Math.Cos(MathHelper.ToRadians((maximum + minimum) * 0.5f));
        }

        public float ScaleMeters { get; private set; }

        public static PlanetDetailMap Create(PlanetDefinitionSnapshot planet)
        {
            if (planet == null || string.IsNullOrWhiteSpace(planet.DetailTexture) ||
                planet.DetailSizeMeters <= 0f || planet.DetailScaleMeters == 0f)
                return null;
            PlanetDetailTexture texture = PlanetContentTextures.GetDetailTexture(planet.DetailTexture);
            return texture == null ? null : new PlanetDetailMap(texture, planet);
        }

        static float Reciprocal(float value)
        {
            return Math.Abs(value) < 1e-6f ? 1e6f : 1f / value;
        }

        public float Sample(float u, float v, float slopeCosine, double footprintMeters, int heightResolution,
            out float riseS, out float riseT)
        {
            riseS = 0f;
            riseT = 0f;
            if (!(slopeCosine >= _minimum && slopeCosine <= _maximum))
                return 0f;
            float weight = slopeCosine > _middle
                ? MathHelper.Clamp(1f - (slopeCosine - _out) * _outReciprocal, 0f, 1f)
                : MathHelper.Clamp((slopeCosine - _in) * _inReciprocal, 0f, 1f);
            if (weight <= 0f)
                return 0f;

            int level = 0;
            double ratio = footprintMeters / _texelMeters;
            while (ratio > 2d && level < _texture.Levels.Length - 1)
            {
                ratio *= 0.5d;
                level++;
            }
            byte[] data = _texture.Levels[level];
            int resolution = _texture.Resolutions[level];

            float x = u * _factor;
            float y = v * _factor;
            x = (x - (float)Math.Floor(x)) * resolution - 0.5f;
            y = (y - (float)Math.Floor(y)) * resolution - 0.5f;
            // In [-0.5, resolution - 0.5]: one comparison wraps each neighbour.
            int ix = (int)(x + 1f) - 1;
            int iy = (int)(y + 1f) - 1;
            float tx = x - ix;
            float ty = y - iy;
            int x0 = ix < 0 ? resolution - 1 : ix;
            int x1 = ix + 1 >= resolution ? 0 : ix + 1;
            int y0 = (iy < 0 ? resolution - 1 : iy) * resolution;
            int y1 = (iy + 1 >= resolution ? 0 : iy + 1) * resolution;
            float a = data[y0 + x0];
            float b = data[y0 + x1];
            float c = data[y1 + x0];
            float d = data[y1 + x1];
            float top = a + (b - a) * tx;
            float bottom = c + (d - c) * tx;
            float value = (top + (bottom - top) * ty) / 255f;

            float amplitude = weight * ScaleMeters;
            // Texture units per detail-uv unit, then detail-uv units per heightmap texel.
            float perTexel = resolution / 255f * _factor / Math.Max(1, heightResolution - 1) * amplitude;
            riseS = ((b - a) * (1f - ty) + (d - c) * ty) * perTexel;
            riseT = (bottom - top) * perTexel;
            return value * amplitude;
        }
    }

    /// <summary>
    /// Game-content textures the surface shading reads, decoded once and shared between planets. The game
    /// itself loads both from its own content folder only (MyHeightMapLoadingSystem, MyPlanetTextureMapProvider).
    /// </summary>
    internal static class PlanetContentTextures
    {
        static readonly object Gate = new object();
        static readonly Dictionary<string, PlanetDetailTexture> DetailTextures =
            new Dictionary<string, PlanetDetailTexture>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, PlanetBlendTileset> BlendTilesets =
            new Dictionary<string, PlanetBlendTileset>(StringComparer.OrdinalIgnoreCase);

        public static void Clear()
        {
            lock (Gate)
            {
                DetailTextures.Clear();
                BlendTilesets.Clear();
            }
        }

        public static PlanetDetailTexture GetDetailTexture(string path)
        {
            lock (Gate)
            {
                PlanetDetailTexture texture;
                if (DetailTextures.TryGetValue(path, out texture))
                    return texture;
                RawPngBitmap bitmap = TryLoad(path);
                // A missing texture is cached as null so later planets do not retry the read.
                texture = bitmap == null || bitmap.Width != bitmap.Height || bitmap.Width < 1
                    ? null
                    : new PlanetDetailTexture(ExtractRed(bitmap), bitmap.Width);
                DetailTextures[path] = texture;
                return texture;
            }
        }

        public static PlanetBlendTileset GetBlendTileset(string path, int cellSize)
        {
            if (string.IsNullOrWhiteSpace(path) || cellSize <= 0)
                return null;
            lock (Gate)
            {
                PlanetBlendTileset tileset;
                string key = path + "|" + cellSize;
                if (BlendTilesets.TryGetValue(key, out tileset))
                    return tileset;
                RawPngBitmap bitmap = TryLoad(path);
                tileset = bitmap == null || bitmap.Width < cellSize * 4 || bitmap.Height < cellSize * 4
                    ? null
                    : new PlanetBlendTileset(ExtractRed(bitmap), bitmap.Width, bitmap.Height, cellSize);
                BlendTilesets[key] = tileset;
                return tileset;
            }
        }

        static RawPngBitmap TryLoad(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var utilities = MyAPIGateway.Utilities;
            if (utilities == null)
                return null;
            string file = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : path + ".png";
            try
            {
                using (var reader = utilities.ReadBinaryFileInGameContent(file))
                    return RawPngBitmap.Load(reader.BaseStream);
            }
            catch (Exception)
            {
                // Optional: without the texture the surface keeps its heightmap shape and texel materials.
                return null;
            }
        }

        static byte[] ExtractRed(RawPngBitmap bitmap)
        {
            var result = new byte[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                int source = y * bitmap.Stride;
                int destination = y * bitmap.Width;
                for (int x = 0; x < bitmap.Width; x++)
                    result[destination + x] = bitmap.Pixels[source + x * 4];
            }
            return result;
        }
    }
}
