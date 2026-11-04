using VRageMath;

namespace Acrux.Render3D
{
    // An axis-aligned box in scene space: what lies outside it is left out, opening the scene up like a section.
    internal struct SceneCut
    {
        public bool Enabled;
        public Vector3D Minimum;
        public Vector3D Maximum;
        // Sides that cut into the content (not merely around it): bits 0-2 are the minimum X/Y/Z, 3-5 the maximum.
        public int TrimmedSides;

        public bool Contains(Vector3D point)
        {
            return point.X >= Minimum.X && point.X <= Maximum.X &&
                   point.Y >= Minimum.Y && point.Y <= Maximum.Y &&
                   point.Z >= Minimum.Z && point.Z <= Maximum.Z;
        }

        public bool TouchesTrimmedSide(Vector3D center, double radius)
        {
            if (TrimmedSides == 0)
                return false;
            return Near(0, center.X - Minimum.X, radius) || Near(1, center.Y - Minimum.Y, radius) ||
                   Near(2, center.Z - Minimum.Z, radius) || Near(3, Maximum.X - center.X, radius) ||
                   Near(4, Maximum.Y - center.Y, radius) || Near(5, Maximum.Z - center.Z, radius);
        }

        bool Near(int side, double distance, double radius)
        {
            return (TrimmedSides & (1 << side)) != 0 && distance <= radius;
        }
    }
}
