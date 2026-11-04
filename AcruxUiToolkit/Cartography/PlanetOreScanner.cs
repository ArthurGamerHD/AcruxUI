using System;
using System.Collections.Generic;
using ProceduralCubemapApi.Api;

namespace Acrux.Cartography
{
    internal static class PlanetOreScanner
    {
        public static PlanetOreDistributionResult Scan(
            CartographyRequest request,
            PlanetDefinitionSnapshot planet,
            PlanetOreCatalogSnapshot catalog,
            PlanetMetadataProvider runtimePlanetMetadata,
            CartographyCancellation cancellation)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (planet == null)
                throw new ArgumentNullException(nameof(planet));
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));
            if (cancellation == null)
                throw new ArgumentNullException(nameof(cancellation));

            PlanetOreMapSnapshot map = PlanetMapSource.LoadOreMap(
                planet,
                cancellation,
                runtimePlanetMetadata);

            int resolution = map.Resolution;
            if (resolution <= 0)
                throw new InvalidOperationException("Ore cubemap has no usable resolution.");

            var countsByValue = new long[256];
            var solidAngleByValue = new double[256];
            var isOreValue = new bool[256];
            foreach (byte value in catalog.ByMapValue.Keys)
                isOreValue[value] = true;

            long totalTexels = 0L;
            long oreMarkedTexels = 0L;
            double totalSolidAngle = 0d;
            double oreMarkedSolidAngle = 0d;

            // A cubemap texel does not cover the same amount of spherical surface
            // everywhere on a face. Texels toward face edges/corners subtend a
            // smaller solid angle than texels near the face center. Count raw
            // texels for diagnostics, but use exact cubemap texel solid angles for
            // all reported surface percentages.
            //
            // For cube-face coordinates u,v in [-1,+1], the exact solid angle of
            // a rectangle is obtained by inclusion/exclusion of:
            //   F(u,v) = atan2(u*v, sqrt(1 + u*u + v*v))
            // Across six complete faces these weights sum to 4*pi steradians.
            var previousBoundary = new double[resolution + 1];
            var currentBoundary = new double[resolution + 1];
            FillSolidAnglePrimitiveRow(previousBoundary, resolution, -1d);

            for (int y = 0; y < resolution; y++)
            {
                cancellation.ThrowIfCancelled();

                double v1 = -1d + (2d * (y + 1) / resolution);
                FillSolidAnglePrimitiveRow(currentBoundary, resolution, v1);
                int rowOffset = y * resolution;

                for (int x = 0; x < resolution; x++)
                {
                    double solidAngle =
                        currentBoundary[x + 1] - currentBoundary[x] -
                        previousBoundary[x + 1] + previousBoundary[x];

                    // The analytical result is positive for monotonically ordered
                    // bounds. Protect against tiny negative values from floating
                    // point cancellation at symmetric boundaries.
                    if (solidAngle < 0d)
                        solidAngle = -solidAngle;

                    int sampleIndex = rowOffset + x;
                    for (int faceIndex = 0; faceIndex < map.Faces.Length; faceIndex++)
                    {
                        byte[] face = map.Faces[faceIndex];
                        if (face == null || sampleIndex >= face.Length)
                            continue;

                        byte value = face[sampleIndex];
                        countsByValue[value]++;
                        solidAngleByValue[value] += solidAngle;
                        totalTexels++;
                        totalSolidAngle += solidAngle;

                        if (isOreValue[value])
                        {
                            oreMarkedTexels++;
                            oreMarkedSolidAngle += solidAngle;
                        }
                    }
                }

                double[] swap = previousBoundary;
                previousBoundary = currentBoundary;
                currentBoundary = swap;
            }

            var countsByOre = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var solidAngleByOre = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var definitionsByOre = new Dictionary<string, OreDefinitionSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<byte, OreDefinitionSnapshot[]> pair in catalog.ByMapValue)
            {
                long valueCount = countsByValue[pair.Key];
                double valueSolidAngle = solidAngleByValue[pair.Key];
                if ((valueCount <= 0L && valueSolidAngle <= 0d) || pair.Value == null)
                    continue;

                for (int i = 0; i < pair.Value.Length; i++)
                {
                    OreDefinitionSnapshot definition = pair.Value[i];
                    if (definition == null || string.IsNullOrWhiteSpace(definition.OreSubtype))
                        continue;

                    long currentCount;
                    countsByOre.TryGetValue(definition.OreSubtype, out currentCount);
                    countsByOre[definition.OreSubtype] = currentCount + valueCount;

                    double currentSolidAngle;
                    solidAngleByOre.TryGetValue(definition.OreSubtype, out currentSolidAngle);
                    solidAngleByOre[definition.OreSubtype] = currentSolidAngle + valueSolidAngle;

                    if (!definitionsByOre.ContainsKey(definition.OreSubtype))
                        definitionsByOre.Add(definition.OreSubtype, definition);
                }
            }

            var entries = new List<PlanetOreDistributionEntry>(countsByOre.Count);
            foreach (KeyValuePair<string, long> pair in countsByOre)
            {
                OreDefinitionSnapshot definition = definitionsByOre[pair.Key];
                double oreSolidAngle;
                solidAngleByOre.TryGetValue(pair.Key, out oreSolidAngle);
                entries.Add(new PlanetOreDistributionEntry
                {
                    OreSubtype = definition.OreSubtype,
                    DisplayName = definition.DisplayName,
                    IconPath = definition.IconPath,
                    TexelCount = pair.Value,
                    SphericalAreaSteradians = oreSolidAngle,
                    SurfaceMapPercent = totalSolidAngle <= 0d
                        ? 0d
                        : oreSolidAngle * 100d / totalSolidAngle
                });
            }

            entries.Sort(delegate(PlanetOreDistributionEntry left, PlanetOreDistributionEntry right)
            {
                int compare = right.SurfaceMapPercent.CompareTo(left.SurfaceMapPercent);
                if (compare != 0)
                    return compare;
                return string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            return new PlanetOreDistributionResult
            {
                Success = true,
                PlanetEntityId = request.PlanetEntityId,
                PlanetGeneratorSubtype = planet.GeneratorSubtype,
                FaceResolution = map.Resolution,
                TotalTexels = totalTexels,
                OreMarkedTexels = oreMarkedTexels,
                TotalSolidAngleSteradians = totalSolidAngle,
                OreMarkedSolidAngleSteradians = oreMarkedSolidAngle,
                TotalOreSurfaceMapPercent = totalSolidAngle <= 0d
                    ? 0d
                    : oreMarkedSolidAngle * 100d / totalSolidAngle,
                Ores = entries.ToArray()
            };
        }

        static void FillSolidAnglePrimitiveRow(double[] destination, int resolution, double v)
        {
            for (int x = 0; x <= resolution; x++)
            {
                double u = -1d + (2d * x / resolution);
                destination[x] = Math.Atan2(
                    u * v,
                    Math.Sqrt(1d + u * u + v * v));
            }
        }
    }
}
