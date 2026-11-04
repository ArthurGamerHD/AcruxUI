using System;
using System.Collections.Generic;
using Acrux.Render3D.Voxels;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRageMath;

namespace Acrux.Cartography
{
    /// <summary>
    /// Far color of every voxel material by its index, for the 3D voxel layer. Explicit far colors are known
    /// at once; the rest come from the same texture fallbacks as the planet maps, resolved in the background.
    /// </summary>
    internal sealed class VoxelMaterialPalette : IVoxelMaterialColors
    {
        public static readonly VoxelMaterialPalette Shared = new VoxelMaterialPalette();

        Vector3 IVoxelMaterialColors.Get(byte materialIndex)
        {
            return Get(materialIndex);
        }

        void IVoxelMaterialColors.EnsureStarted()
        {
            EnsureStarted();
        }

        bool IVoxelMaterialColors.IsLoading
        {
            get { return IsBuilding; }
        }

        static readonly Vector3 Unknown = new Vector3(0.42f, 0.40f, 0.38f);
        static volatile Vector3[] _colors;
        static volatile int _generation;
        static volatile bool _building;
        static bool _started;

        public static bool IsBuilding
        {
            get { return _building; }
        }

        public static void Clear()
        {
            _generation++;
            _colors = null;
            _started = false;
        }

        public static Vector3 Get(byte index)
        {
            Vector3[] colors = _colors;
            return colors == null ? Unknown : colors[index];
        }

        // An XML round-trip per definition, and texture reads: far too slow for a game tick. Voxels draw neutral
        // until it is done.
        public static void EnsureStarted()
        {
            if (_started)
                return;
            _started = true;
            int generation = ++_generation;
            _building = true;
            try
            {
                MyAPIGateway.Parallel.StartBackground(() => Build(generation));
            }
            catch (Exception)
            {
                _started = false;
                _building = false;
            }
        }

        static void Build(int generation)
        {
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var byIndex = new string[256];
                foreach (MyVoxelMaterialDefinition definition in MyDefinitionManager.Static.GetVoxelMaterialDefinitions())
                {
                    if (definition == null)
                        continue;
                    string name = definition.Id.SubtypeName;
                    names.Add(name);
                    byIndex[definition.Index] = name;
                }

                FarColorCatalogSnapshot catalog = CartographySnapshotBuilder.BuildFarColors(names, false);
                if (generation != _generation)
                    return;
                // Explicit far colors first, then again once the texture fallbacks are resolved.
                _colors = ToPalette(catalog, byIndex);
                catalog.ResolveTextureFallbacks(new CartographyCancellation());
                if (generation == _generation)
                    _colors = ToPalette(catalog, byIndex);
            }
            catch (Exception)
            {
                // Keep whatever was published; voxels without a color stay rock-grey.
            }
            finally
            {
                if (generation == _generation)
                    _building = false;
            }
        }

        static Vector3[] ToPalette(FarColorCatalogSnapshot catalog, string[] byIndex)
        {
            var colors = new Vector3[256];
            for (int i = 0; i < 256; i++)
            {
                Color color;
                colors[i] = byIndex[i] != null && !catalog.IsMissing(byIndex[i]) && catalog.TryGet(byIndex[i], out color)
                    ? color.ToVector3()
                    : Unknown;
            }
            return colors;
        }
    }
}
