using System;
using System.Collections.Generic;
using System.IO;
using Adk.Image;
using VRageMath;

namespace Acrux.Render3D.Models
{
    /// <summary>
    /// A content model's lowest-detail LOD, ready to draw: its vertices in model metres, one index list and one
    /// texture tint per material, glass left out. Never changed once loaded, except for <see cref="Edges"/>.
    /// </summary>
    internal sealed class MeshAsset
    {
        public Vector3[] Vertices;
        public int[][] PartIndices;
        public Vector3[] PartTints;
        public int TriangleCount;
        // Glass was left out of it (windows, canopies): its box would close what the model leaves open.
        public bool HasGlass;
        // Bounding sphere of the vertices, in model metres.
        public Vector3 Center;
        public float Radius;
        // Its outline as pairs of ends, built the first time a wireframe asks for it.
        public volatile Vector3[] Edges;
    }

    /// <summary>
    /// Every 3D layer's content models, loaded on the game thread (content files are read through the game's
    /// file API) and shared by all frames. A failed load is retried after a while: a model the game was still
    /// streaming at startup is picked up later instead of never.
    /// </summary>
    internal static class MeshAssets
    {
        const double RETRY_SECONDS = 20d;

        static readonly Dictionary<string, MeshAsset> Cache =
            new Dictionary<string, MeshAsset>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> Failed =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Vector3> TextureTints =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        public static void Release()
        {
            Cache.Clear();
            Failed.Clear();
            TextureTints.Clear();
        }

        // A LOD this many times coarser than the next finer one is an impostor: a few crossed cards standing in for
        // the whole model at range (every vanilla tree's last LOD, 12 to 16 triangles after hundreds).
        const int IMPOSTOR_COLLAPSE = 8;

        // Game thread. The loaded model, or null while it is not: each load not yet cached spends one of loads.
        // With skipImpostors the coarsest LOD that is still the model's real shape is drawn instead of a last LOD
        // that collapses to an impostor (vegetation); it is cached apart from the plain load.
        public static MeshAsset Resolve(string assetName, ref int loads, bool skipImpostors = false)
        {
            if (string.IsNullOrEmpty(assetName))
                return null;
            string key = Key(assetName, skipImpostors);
            MeshAsset model;
            if (Cache.TryGetValue(key, out model))
                return model;
            DateTime retry;
            DateTime now = DateTime.UtcNow;
            if (Failed.TryGetValue(key, out retry) && now < retry)
                return null;
            if (loads <= 0)
                return null;
            loads--;

            model = Load(assetName, skipImpostors);
            if (model != null)
            {
                Cache[key] = model;
                Failed.Remove(key);
            }
            else
            {
                Failed[key] = now.AddSeconds(RETRY_SECONDS);
            }
            return model;
        }

        static string Key(string assetName, bool skipImpostors)
        {
            return skipImpostors ? assetName + "|solid" : assetName;
        }

        // Game thread. The model if it is loaded already, without loading it.
        public static MeshAsset Loaded(string assetName)
        {
            MeshAsset model;
            return !string.IsNullOrEmpty(assetName) && Cache.TryGetValue(assetName, out model) ? model : null;
        }

        // Game thread. The last load of this model failed and is not due again yet.
        public static bool HasFailed(string assetName, bool skipImpostors = false)
        {
            return !string.IsNullOrEmpty(assetName) && Failed.ContainsKey(Key(assetName, skipImpostors));
        }

        // The lowest-detail LOD the model lists, or the model itself: a map needs the whole shape, not its detail.
        // With skipImpostors, a LOD that collapses to an impostor is passed over for the next finer one.
        static MinimalMwmMesh ReadLowestLod(string modelPath, bool skipImpostors)
        {
            List<string> lodPaths = null;
            MinimalMwmMesh mesh = null;
            using (BinaryReader reader = ContentFiles.Open(modelPath))
            {
                if (reader == null)
                    return null;
                MinimalMwmReader.TryReadLodPaths(reader, out lodPaths);
                reader.BaseStream.Position = 0;
                if (lodPaths == null || lodPaths.Count == 0)
                    return MinimalMwmReader.TryReadMesh(reader, out mesh) ? mesh : null;
            }
            // Coarsest first; finer is the next finer LOD (the model itself after the first), read once each.
            MinimalMwmMesh finer = null;
            bool finerRead = false;
            for (int i = lodPaths.Count - 1; i >= 0; i--)
            {
                if (finerRead)
                {
                    mesh = finer;
                    finerRead = false;
                }
                else
                {
                    mesh = ReadMesh(ContentFiles.ToModelPath(lodPaths[i] + ".mwm"));
                }
                if (mesh == null)
                    continue;
                if (!skipImpostors)
                    return mesh;
                finer = i > 0 ? ReadMesh(ContentFiles.ToModelPath(lodPaths[i - 1] + ".mwm")) : ReadMesh(modelPath);
                finerRead = i > 0;
                if (finer == null || (long)TriangleCount(mesh) * IMPOSTOR_COLLAPSE >= TriangleCount(finer))
                    return mesh;
                if (i == 0)
                    return finer;
            }
            return ReadMesh(modelPath);
        }

        static MinimalMwmMesh ReadMesh(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            MinimalMwmMesh mesh;
            using (BinaryReader reader = ContentFiles.Open(path))
                return reader != null && MinimalMwmReader.TryReadMesh(reader, out mesh) ? mesh : null;
        }

        static int TriangleCount(MinimalMwmMesh mesh)
        {
            int count = 0;
            for (int i = 0; i < mesh.Parts.Count; i++)
                count += mesh.Parts[i].Indices == null ? 0 : mesh.Parts[i].Indices.Length / 3;
            return count;
        }

        static MeshAsset Load(string assetName, bool skipImpostors)
        {
            try
            {
                MinimalMwmMesh mesh = ReadLowestLod(ContentFiles.ToModelPath(assetName), skipImpostors);
                if (mesh == null || mesh.Vertices == null || mesh.Vertices.Length == 0)
                    return null;

                // Glass would draw as an opaque white sheet: the rasterizer has no transparency, so windows
                // are left open and what is behind them shows through.
                var parts = new List<MinimalMwmMeshPart>(mesh.Parts.Count);
                bool glass = false;
                for (int i = 0; i < mesh.Parts.Count; i++)
                {
                    if (IsGlass(mesh.Parts[i]))
                        glass = true;
                    else
                        parts.Add(mesh.Parts[i]);
                }

                var model = new MeshAsset
                {
                    HasGlass = glass,
                    Vertices = mesh.Vertices,
                    PartIndices = new int[parts.Count][],
                    PartTints = new Vector3[parts.Count]
                };
                for (int i = 0; i < parts.Count; i++)
                {
                    model.PartIndices[i] = parts[i].Indices;
                    model.PartTints[i] = ResolveTextureTint(parts[i].ColorMetalTexture);
                    model.TriangleCount += parts[i].Indices.Length / 3;
                }

                Vector3 minimum = new Vector3(float.MaxValue);
                Vector3 maximum = new Vector3(float.MinValue);
                for (int v = 0; v < mesh.Vertices.Length; v++)
                {
                    minimum = Vector3.Min(minimum, mesh.Vertices[v]);
                    maximum = Vector3.Max(maximum, mesh.Vertices[v]);
                }
                model.Center = (minimum + maximum) * 0.5f;
                model.Radius = (maximum - minimum).Length() * 0.5f;
                return model;
            }
            catch
            {
                return null;
            }
        }

        // Vanilla window, cockpit and canopy glass materials are all named for it (Glass, CockpitGlass,
        // GlassOutside, ...), as are their textures.
        static bool IsGlass(MinimalMwmMeshPart part)
        {
            // By technique first: a model's far LODs keep the GLASS technique but may rename the material.
            return string.Equals(part.Technique, "GLASS", StringComparison.OrdinalIgnoreCase) ||
                   Contains(part.Material, "glass") || Contains(part.ColorMetalTexture, "glass");
        }

        static bool Contains(string text, string word)
        {
            return text != null && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static Vector3 ResolveTextureTint(string texture)
        {
            if (string.IsNullOrEmpty(texture))
                return Vector3.One;
            Vector3 tint;
            if (TextureTints.TryGetValue(texture, out tint))
                return tint;

            tint = Vector3.One;
            try
            {
                using (BinaryReader reader = ContentFiles.Open(texture))
                {
                    byte r;
                    byte g;
                    byte b;
                    if (reader != null && DdsAverageColor.TryAverageSmallestMip(reader.BaseStream, out r, out g, out b))
                        tint = new Vector3(r / 255f, g / 255f, b / 255f);
                }
            }
            catch
            {
                tint = Vector3.One;
            }
            TextureTints[texture] = tint;
            return tint;
        }
    }
}
