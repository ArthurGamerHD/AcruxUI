using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// A perspective pinhole camera. The basis is right-handed with the camera looking along
    /// <see cref="Forward"/>; depth is the distance along it, in the same metres as the scene.
    /// </summary>
    internal struct SceneCamera
    {
        public Vector3D Position;
        public Vector3D Right;
        public Vector3D Up;
        public Vector3D Forward;
        public double FocalLengthPixels;
        public double CenterX;
        public double CenterY;
        public double NearDepth;
        public int Width;
        public int Height;

        public void ToCameraSpace(ref Vector3D point, out SceneVertex vertex)
        {
            double dx = point.X - Position.X;
            double dy = point.Y - Position.Y;
            double dz = point.Z - Position.Z;
            vertex = default(SceneVertex);
            vertex.X = dx * Right.X + dy * Right.Y + dz * Right.Z;
            vertex.Y = dx * Up.X + dy * Up.Y + dz * Up.Z;
            vertex.Z = dx * Forward.X + dy * Forward.Y + dz * Forward.Z;
        }

        // radiusPixels is infinite for spheres around or just ahead of the camera, which always may.
        public bool MayBeOnScreen(ref SceneVertex center, double radius, out double radiusPixels)
        {
            radiusPixels = double.PositiveInfinity;
            if (center.Z < -radius)
                return false;
            if (center.Z <= radius)
                return true;
            double scale = FocalLengthPixels / center.Z;
            double x = CenterX + center.X * scale;
            double y = CenterY - center.Y * scale;
            // A little generous for the sphere's perspective.
            radiusPixels = radius * scale * 1.2d;
            return x + radiusPixels >= 0d && x - radiusPixels <= Width &&
                   y + radiusPixels >= 0d && y - radiusPixels <= Height;
        }

        public bool TryProject(Vector3D point, out Vector2 pixel, out double depth)
        {
            SceneVertex vertex;
            ToCameraSpace(ref point, out vertex);
            depth = vertex.Z;
            if (!(vertex.Z > NearDepth))
            {
                pixel = Vector2.Zero;
                return false;
            }

            pixel = new Vector2(
                (float)(CenterX + FocalLengthPixels * vertex.X / vertex.Z),
                (float)(CenterY - FocalLengthPixels * vertex.Y / vertex.Z));
            return true;
        }

        public Vector3D GetRay(Vector2 pixel)
        {
            Vector3D ray = Forward * FocalLengthPixels +
                           Right * (pixel.X - CenterX) +
                           Up * (CenterY - pixel.Y);
            ray.Normalize();
            return ray;
        }
    }

    /// <summary>
    /// A vertex in camera space (X right, Y up, Z depth) with the values interpolated across its triangle:
    /// a three-component attribute (a cubemap direction, or a lit color) and a shade factor.
    /// </summary>
    internal struct SceneVertex
    {
        public double X;
        public double Y;
        public double Z;
        public Vector3 Attribute;
        public float Shade;
    }
}
