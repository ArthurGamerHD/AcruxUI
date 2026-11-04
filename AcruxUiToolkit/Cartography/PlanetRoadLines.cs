using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRageMath;

namespace Acrux.Cartography
{
    // Read-only XML shape of the Voxel Cubemap API's GetPlanetModificationsXml
    // (API 0.0.17+). XmlSerializer ignores everything else; class names must
    // match the API's element names.
    public sealed class PlanetModificationListing
    {
        public List<PlanetModificationInfo> Modifications;
    }

    public sealed class PlanetModificationInfo
    {
        public string Id;
        public List<RuntimeProceduralLineOperation> Lines;
    }

    public sealed class RuntimeProceduralLineOperation
    {
        public string Layer;
        public double[] Geometry;
        public double WidthMeters;
    }

    internal static class PlanetRoadLineParser
    {
        public static List<PlanetRoadLine> ParseListing(string xml)
        {
            var roads = new List<PlanetRoadLine>();
            if (string.IsNullOrWhiteSpace(xml))
                return roads;
            var listing = MyAPIGateway.Utilities.SerializeFromXML<PlanetModificationListing>(xml);
            if (listing == null || listing.Modifications == null)
                return roads;
            foreach (PlanetModificationInfo modification in listing.Modifications)
            {
                if (modification == null || modification.Lines == null) continue;
                foreach (RuntimeProceduralLineOperation line in modification.Lines)
                {
                    PlanetRoadLine road;
                    if (line != null &&
                        string.Equals(line.Layer, "material", StringComparison.OrdinalIgnoreCase) &&
                        TryCreate(line.Geometry, line.WidthMeters, out road))
                        roads.Add(road);
                }
            }
            return roads;
        }

        static bool TryCreate(double[] g, double widthMeters, out PlanetRoadLine road)
        {
            road = new PlanetRoadLine();
            if (g == null || g.Length != 12) return false;
            for (int i = 0; i < 12; i++)
                if (double.IsNaN(g[i]) || double.IsInfinity(g[i])) return false;
            Vector3D a = new Vector3D(g[0], g[1], g[2]);
            Vector3D b = new Vector3D(g[3], g[4], g[5]);
            if (a.Normalize() < 1e-12 || b.Normalize() < 1e-12) return false;
            Vector3D da = new Vector3D(g[6], g[7], g[8]);
            Vector3D db = new Vector3D(g[9], g[10], g[11]);
            da -= a * Vector3D.Dot(da, a);
            db -= b * Vector3D.Dot(db, b);
            if (da.Normalize() < 1e-8 || db.Normalize() < 1e-8) return false;
            double arc = Math.Acos(MathHelper.Clamp(Vector3D.Dot(a, b), -1.0, 1.0));
            if (arc < 1e-7 || arc > Math.PI - 1e-5) return false;
            road.Start = a;
            road.End = b;
            road.StartTangent = da;
            road.EndTangent = db;
            road.Arc = arc;
            road.WidthMeters = widthMeters;
            return true;
        }
    }
}
