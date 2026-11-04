using System;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// A triangle mesh a client mod draws in a 3D scene (a preview of something it is about to build, a marker
    /// with a real shape). The mesh is in the model's own metres and placed in the world by
    /// <see cref="WorldMatrix"/>; it is depth-tested against the terrain, grids and voxels of the scene.
    /// Add it to a scene with <see cref="Acrux.Cartography.Maps.CartographyMapRenderer.AddModel"/>. The renderer
    /// reads the model when it captures a frame: after changing it, ask the renderer for a new frame.
    /// </summary>
    public sealed class SceneModel
    {
        /// <summary>The most triangles one model may hold.</summary>
        public const int MAXIMUM_TRIANGLES = 200000;

        // Replaced as a whole and never changed afterwards, so a frame still painting keeps the one it captured.
        internal sealed class Mesh
        {
            public Vector3[] Vertices;
            public int[] Indices;
            public Color[] TriangleColors;
            public Vector3 Center;
            public float Radius;
        }

        Mesh _mesh;

        /// <summary>Places the mesh in the world. Identity leaves the vertices as world positions relative to the origin.</summary>
        public MatrixD WorldMatrix { get; set; } = MatrixD.Identity;

        /// <summary>The color of every triangle without a color of its own. Its alpha is multiplied by <see cref="Opacity"/>.</summary>
        public Color Color { get; set; } = Color.White;

        /// <summary>From 0 (invisible) to 1 (solid). Below 1 the model blends over what lies behind it.</summary>
        public float Opacity { get; set; } = 1f;

        /// <summary>False leaves the model out of the scene without removing it.</summary>
        public bool Visible { get; set; } = true;

        /// <summary>True draws the colors as given; false shades each triangle by the scene's light.</summary>
        public bool Unlit { get; set; }

        /// <summary>The triangles of the current mesh.</summary>
        public int TriangleCount
        {
            get
            {
                Mesh mesh = _mesh;
                return mesh == null ? 0 : mesh.Indices.Length / 3;
            }
        }

        internal Mesh Current
        {
            get { return _mesh; }
        }

        /// <summary>
        /// Replaces the mesh: <paramref name="vertices"/> in model metres, and three <paramref name="indices"/> into
        /// them per triangle (both sides of a triangle are drawn). The arrays are copied. False, leaving the
        /// mesh as it was, when an index is out of range, the index count is not a multiple of three or there
        /// are more than <see cref="MAXIMUM_TRIANGLES"/> triangles.
        /// </summary>
        public bool SetMesh(Vector3[] vertices, int[] indices)
        {
            return SetMesh(vertices, indices, null);
        }

        /// <summary>
        /// As <see cref="SetMesh(Vector3[], int[])"/>, with one color per triangle in <paramref name="triangleColors"/>
        /// (null uses <see cref="Color"/> for all of them). False when its length is not the triangle count.
        /// </summary>
        public bool SetMesh(Vector3[] vertices, int[] indices, Color[] triangleColors)
        {
            if (vertices == null || indices == null || vertices.Length == 0 || indices.Length == 0 ||
                indices.Length % 3 != 0 || indices.Length / 3 > MAXIMUM_TRIANGLES ||
                (triangleColors != null && triangleColors.Length != indices.Length / 3))
                return false;
            Vector3 minimum = new Vector3(float.MaxValue);
            Vector3 maximum = new Vector3(float.MinValue);
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 vertex = vertices[i];
                if (float.IsNaN(vertex.X) || float.IsInfinity(vertex.X) ||
                    float.IsNaN(vertex.Y) || float.IsInfinity(vertex.Y) ||
                    float.IsNaN(vertex.Z) || float.IsInfinity(vertex.Z))
                    return false;
                minimum = Vector3.Min(minimum, vertex);
                maximum = Vector3.Max(maximum, vertex);
            }
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] < 0 || indices[i] >= vertices.Length)
                    return false;
            }

            var mesh = new Mesh
            {
                Vertices = new Vector3[vertices.Length],
                Indices = new int[indices.Length],
                Center = (minimum + maximum) * 0.5f,
                Radius = (maximum - minimum).Length() * 0.5f
            };
            Array.Copy(vertices, mesh.Vertices, vertices.Length);
            Array.Copy(indices, mesh.Indices, indices.Length);
            if (triangleColors != null)
            {
                mesh.TriangleColors = new Color[triangleColors.Length];
                Array.Copy(triangleColors, mesh.TriangleColors, triangleColors.Length);
            }
            _mesh = mesh;
            return true;
        }

        /// <summary>Removes the mesh; the model draws nothing until the next <see cref="SetMesh(Vector3[], int[])"/>.</summary>
        public void ClearMesh()
        {
            _mesh = null;
        }
    }
}
