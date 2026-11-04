using System;
using System.Collections.Generic;
using Adk.Image.Png;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using ProceduralCubemapApi.Api;
using PlanetGeneratorDefinition = Sandbox.Definitions.MyPlanetGeneratorDefinition;
using VoxelMaterialDefinition = VRage.Game.MyVoxelMaterialDefinition;
using InvalidDataException = Adk.Compression.Exceptions.InvalidDataException;

// Ore survey: the share of a planet's surface map marked with each ore.
namespace Acrux.Cartography
{
    public sealed class PlanetOreDistributionEntry
    {
        public string OreSubtype;
        public string DisplayName;
        public string IconPath;
        // Raw texel count is retained for diagnostics only. Cubemap texels are
        // not equal-area samples on a sphere, so UI percentages use the solid
        // angle accumulated in SphericalAreaSteradians instead.
        public long TexelCount;
        public double SphericalAreaSteradians;
        public double SurfaceMapPercent;
    }

    public sealed class PlanetOreDistributionResult
    {
        public bool Success;
        public bool Cancelled;
        public string Error;
        public long PlanetEntityId;
        public string PlanetGeneratorSubtype;
        public int FaceResolution;
        public long TotalTexels;
        public long OreMarkedTexels;
        public double TotalSolidAngleSteradians;
        public double OreMarkedSolidAngleSteradians;
        public double TotalOreSurfaceMapPercent;
        public PlanetOreDistributionEntry[] Ores;
    }

    internal sealed class OreDefinitionSnapshot
    {
        public byte MapValue;
        public string OreSubtype;
        public string DisplayName;
        public string IconPath;
    }

    internal sealed class PlanetOreCatalogSnapshot
    {
        public readonly Dictionary<byte, OreDefinitionSnapshot[]> ByMapValue =
            new Dictionary<byte, OreDefinitionSnapshot[]>();
    }

    internal sealed class PlanetOreMapSnapshot
    {
        public readonly byte[][] Faces = new byte[6][];
        public int Resolution;
    }

    public sealed partial class CartographyModule
    {
        public CartographyTicket RequestOreDistribution(
            CartographyRequest request,
            Action<PlanetOreDistributionResult> completed)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            CartographyRequest workRequest = CopyRequest(request);
            string requestedPlanetType = ResolvePlanetType(workRequest);
            RuntimePlanetMetadataRegistration runtimePlanetMetadata =
                EnsureRuntimePlanetMetadata(workRequest, requestedPlanetType);
            PlanetDefinitionSnapshot planet = CartographySnapshotBuilder.BuildPlanet(workRequest);
            PlanetOreCatalogSnapshot catalog = CartographySnapshotBuilder.BuildOreCatalog(workRequest);
            var cancellation = new CartographyCancellation();
            long ticketId;
            lock (_sync)
            {
                if (_unloaded)
                    throw new InvalidOperationException("Cartography module is unloaded.");
                ticketId = ++_nextId;
            }

            PlanetOreDistributionResult result = null;
            MyAPIGateway.Parallel.Start(
                delegate
                {
                    try
                    {
                        cancellation.ThrowIfCancelled();
                        result = PlanetOreScanner.Scan(
                            workRequest,
                            planet,
                            catalog,
                            runtimePlanetMetadata == null
                                ? null
                                : runtimePlanetMetadata.Provider,
                            cancellation);
                    }
                    catch (CartographyCancelledException)
                    {
                        result = new PlanetOreDistributionResult
                        {
                            Success = false,
                            Cancelled = true,
                            PlanetEntityId = workRequest.PlanetEntityId,
                            PlanetGeneratorSubtype = planet.GeneratorSubtype,
                            Ores = new PlanetOreDistributionEntry[0]
                        };
                    }
                    catch (Exception error)
                    {
                        result = new PlanetOreDistributionResult
                        {
                            Success = false,
                            Error = error.Message,
                            PlanetEntityId = workRequest.PlanetEntityId,
                            PlanetGeneratorSubtype = planet.GeneratorSubtype,
                            Ores = new PlanetOreDistributionEntry[0]
                        };
                    }
                },
                delegate
                {
                    if (completed != null)
                        completed(result ?? new PlanetOreDistributionResult
                        {
                            Success = false,
                            Error = "Ore scan did not produce a result.",
                            PlanetEntityId = workRequest.PlanetEntityId,
                            PlanetGeneratorSubtype = planet.GeneratorSubtype,
                            Ores = new PlanetOreDistributionEntry[0]
                        });
                });

            return new CartographyTicket(ticketId, cancellation.Cancel);
        }
    }

    internal static partial class CartographySnapshotBuilder
    {
        public static PlanetOreCatalogSnapshot BuildOreCatalog(CartographyRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (MyDefinitionManager.Static == null)
                throw new InvalidOperationException("Definition manager is not ready.");

            MyPlanet planet = ResolvePlanet(request);
            PlanetGeneratorDefinition generator = planet != null
                ? planet.Generator
                : ResolveGenerator(request.PlanetGeneratorSubtype);
            if (generator == null)
                throw new InvalidOperationException("Planet generator definition was not found.");

            var voxelOreByMaterial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (VoxelMaterialDefinition voxel in MyDefinitionManager.Static.GetVoxelMaterialDefinitions())
            {
                if (voxel == null || string.IsNullOrWhiteSpace(voxel.Id.SubtypeName))
                    continue;
                voxelOreByMaterial[voxel.Id.SubtypeName] = string.IsNullOrWhiteSpace(voxel.MinedOre)
                    ? voxel.Id.SubtypeName
                    : voxel.MinedOre;
            }

            var rows = new Dictionary<byte, List<OreDefinitionSnapshot>>();
            if (generator.OreMappings != null)
            {
                for (int i = 0; i < generator.OreMappings.Length; i++)
                {
                    var mapping = generator.OreMappings[i];
                    string oreSubtype;
                    if (string.IsNullOrWhiteSpace(mapping.Type) ||
                        !voxelOreByMaterial.TryGetValue(mapping.Type, out oreSubtype))
                    {
                        oreSubtype = string.IsNullOrWhiteSpace(mapping.Type)
                            ? "Unknown"
                            : mapping.Type;
                    }

                    string displayName = oreSubtype;
                    string iconPath = ResolveOreIconMaterial(oreSubtype);

                    List<OreDefinitionSnapshot> valueRows;
                    if (!rows.TryGetValue(mapping.Value, out valueRows))
                    {
                        valueRows = new List<OreDefinitionSnapshot>();
                        rows.Add(mapping.Value, valueRows);
                    }

                    bool duplicate = false;
                    for (int rowIndex = 0; rowIndex < valueRows.Count; rowIndex++)
                    {
                        if (string.Equals(
                            valueRows[rowIndex].OreSubtype,
                            oreSubtype,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (!duplicate)
                    {
                        valueRows.Add(new OreDefinitionSnapshot
                        {
                            MapValue = mapping.Value,
                            OreSubtype = oreSubtype,
                            DisplayName = displayName,
                            IconPath = iconPath
                        });
                    }
                }
            }

            var result = new PlanetOreCatalogSnapshot();
            foreach (KeyValuePair<byte, List<OreDefinitionSnapshot>> pair in rows)
                result.ByMapValue[pair.Key] = pair.Value.ToArray();
            return result;
        }

        static string ResolveOreIconMaterial(string oreSubtype)
        {
            if (string.IsNullOrWhiteSpace(oreSubtype))
                return null;

            switch (oreSubtype.Trim().ToLowerInvariant())
            {
                case "iron": return "Minimap_Ore_Iron";
                case "nickel": return "Minimap_Ore_Nickel";
                case "cobalt": return "Minimap_Ore_Cobalt";
                case "magnesium": return "Minimap_Ore_Magnesium";
                case "silicon": return "Minimap_Ore_Silicon";
                case "silver": return "Minimap_Ore_Silver";
                case "gold": return "Minimap_Ore_Gold";
                case "platinum": return "Minimap_Ore_Platinum";
                case "uranium": return "Minimap_Ore_Uranium";
                case "ice": return "Minimap_Ore_Ice";
                default: return null;
            }
        }
    }

    internal sealed partial class PlanetMapSource
    {
        public static PlanetOreMapSnapshot LoadOreMap(
            PlanetDefinitionSnapshot planet,
            CartographyCancellation cancellation,
            PlanetMetadataProvider runtimePlanetMetadata = null)
        {
            if (planet == null)
                throw new ArgumentNullException(nameof(planet));
            if (cancellation == null)
                throw new ArgumentNullException(nameof(cancellation));

            var result = new PlanetOreMapSnapshot();
            int runtimeFaceResolution = 0;
            int fileFaceResolution = 0;
            for (int i = 0; i < ExportOrder.Length; i++)
            {
                cancellation.ThrowIfCancelled();
                PlanetCubeFace face = ExportOrder[i];
                string faceName = GetFaceName(face);

                if (runtimePlanetMetadata != null)
                {
                    if (runtimeFaceResolution == 0)
                    {
                        int[] size = runtimePlanetMetadata.GetFaceSize(faceName);
                        runtimeFaceResolution = ValidateRuntimeFaceSize(size, faceName);
                    }

                    byte[] ore = runtimePlanetMetadata.LoadOreFace(faceName);
                    ValidateRuntimeFaceSamples(ore, runtimeFaceResolution, faceName + "_mat.png");
                    result.Faces[(int)face] = ore;
                    result.Resolution = runtimeFaceResolution;
                }
                else
                {
                    RawPngBitmap material = LoadPng(planet, faceName + "_mat.png");
                    ValidateResolution(
                        material,
                        faceName + "_mat.png",
                        "ore",
                        ref fileFaceResolution);
                    result.Faces[(int)face] = ExtractMaterialChannel(material, 2);
                    result.Resolution = fileFaceResolution;
                }
            }

            if (result.Resolution <= 0)
                throw new InvalidDataException("Ore cartography requires material-map cubemap faces.");
            return result;
        }
    }
}
