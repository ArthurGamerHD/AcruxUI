using VRageMath;

namespace Acrux.Cartography
{
    /// <summary>
    /// One built road segment: the spherical Hermite centerline the Voxel
    /// Cubemap API rasterizes, in planet-local unit directions. View only.
    /// </summary>
    public struct PlanetRoadLine
    {
        public Vector3D Start;
        public Vector3D End;
        public Vector3D StartTangent;
        public Vector3D EndTangent;
        public double Arc;
        public double WidthMeters;

        /// <summary>Matches SphericalLineRasterizer in the Voxel Cubemap API.</summary>
        public Vector3D Evaluate(double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;
            double magnitude = Arc * 0.5;
            Vector3D point = Start * (2 * t3 - 3 * t2 + 1) +
                             StartTangent * magnitude * (t3 - 2 * t2 + t) +
                             End * (-2 * t3 + 3 * t2) +
                             EndTangent * magnitude * (t3 - t2);
            point.Normalize();
            return point;
        }
    }
}
