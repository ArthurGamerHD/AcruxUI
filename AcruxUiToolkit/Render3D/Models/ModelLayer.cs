using System;
using System.Collections.Generic;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Render3D.Models
{
    // Client-supplied meshes (SceneModel) in a scene: flat-shaded triangles, solid or blended over what is behind.
    internal sealed class ModelLayer : ISceneLayer
    {
        const float SOLID_ALPHA = 0.995f;

        struct Item
        {
            public SceneModel.Mesh Mesh;
            public MatrixD ModelToScene;
            public Color Color;
            public float Opacity;
            public bool Unlit;
        }

        readonly List<Item> _items = new List<Item>();
        readonly SceneTriangleList _triangles = new SceneTriangleList();
        Vector3D[] _positions = new Vector3D[0];
        SceneCamera _camera;
        Vector3 _light;

        public string PendingWork
        {
            get { return null; }
        }

        // Game thread. True when a model may be on screen, so the layer is worth adding to the frame.
        public bool Capture(ref SceneCapture capture, List<SceneModel> models)
        {
            _camera = capture.Camera;
            _light = capture.Light;
            _items.Clear();
            for (int i = 0; i < models.Count; i++)
            {
                SceneModel model = models[i];
                SceneModel.Mesh mesh = model == null || !model.Visible ? null : model.Current;
                float opacity = model == null ? 0f : model.Opacity;
                if (mesh == null || !(opacity > 0f))
                    continue;
                MatrixD modelToScene = model.WorldMatrix * capture.WorldToScene;
                Vector3D center = Vector3D.Transform((Vector3D)mesh.Center, modelToScene);
                // A scaled matrix grows the bounding sphere by its longest axis.
                double scale = Math.Sqrt(Math.Max(modelToScene.Right.LengthSquared(),
                    Math.Max(modelToScene.Up.LengthSquared(), modelToScene.Forward.LengthSquared())));
                SceneVertex centerVertex;
                _camera.ToCameraSpace(ref center, out centerVertex);
                double radiusPixels;
                if (!IsFinite(centerVertex.Z) || !_camera.MayBeOnScreen(ref centerVertex, mesh.Radius * scale, out radiusPixels))
                    continue;
                _items.Add(new Item
                {
                    Mesh = mesh,
                    ModelToScene = modelToScene,
                    Color = model.Color,
                    Opacity = Math.Min(1f, opacity),
                    Unlit = model.Unlit
                });
            }
            return _items.Count > 0;
        }

        static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public void Build()
        {
            _triangles.Clear();
            for (int i = 0; i < _items.Count; i++)
                AddModel(_items[i]);
            _triangles.Bin(_camera.Height);
        }

        void AddModel(Item item)
        {
            Vector3[] vertices = item.Mesh.Vertices;
            int[] indices = item.Mesh.Indices;
            Color[] colors = item.Mesh.TriangleColors;
            if (_positions.Length < vertices.Length)
                _positions = new Vector3D[vertices.Length];
            Vector3D[] positions = _positions;
            MatrixD matrix = item.ModelToScene;
            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3D local = vertices[v];
                Vector3D.Transform(ref local, ref matrix, out positions[v]);
            }

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                Vector3D a = positions[indices[t]];
                Vector3D b = positions[indices[t + 1]];
                Vector3D c = positions[indices[t + 2]];
                Color color = colors == null ? item.Color : colors[t / 3];
                float alpha = color.A / 255f * item.Opacity;
                if (!(alpha > 0f))
                    continue;

                // Two-sided, as the grids of the scene are lit: ambient plus a directional term on the face normal.
                float shade = 1f;
                if (!item.Unlit)
                {
                    Vector3 normal = (Vector3)Vector3D.Cross(b - a, c - a);
                    shade = normal.Normalize() > 1e-9f
                        ? MathHelper.Clamp(0.64f + 0.34f * Math.Abs(Vector3.Dot(normal, _light)), 0.34f, 1.05f)
                        : 0.62f;
                }

                SceneVertex va;
                SceneVertex vb;
                SceneVertex vc;
                _camera.ToCameraSpace(ref a, out va);
                _camera.ToCameraSpace(ref b, out vb);
                _camera.ToCameraSpace(ref c, out vc);
                va.Attribute = vb.Attribute = vc.Attribute =
                    new Vector3(color.R, color.G, color.B) * (shade / 255f);
                va.Shade = vb.Shade = vc.Shade = alpha;
                _triangles.Add(ref _camera, ref va, ref vb, ref vc);
            }
        }

        public void Draw(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow)
        {
            int count = _triangles.Count;
            if (depthBuffer == null || count == 0)
                return;
            ScreenTriangle[] items = _triangles.Items;
            int width = canvas.Width;
            int lastRow = Math.Min(canvas.Height, endRow) - 1;
            Action<int> raster = i =>
            {
                int minY = Math.Max(items[i].MinY, firstRow);
                int maxY = Math.Min(items[i].MaxY, lastRow);
                if (minY > maxY)
                    return;

                ScreenTriangle t = items[i];
                // Flat triangles carry the lit color and the alpha pre-divided by depth at each vertex.
                Vector3 lit = t.A0 / t.InverseZ0;
                float alpha = t.S0 / t.InverseZ0;
                bool solid = alpha >= SOLID_ALPHA;
                var color = new Color(
                    (byte)MathHelper.Clamp(lit.X * 255f, 0f, 255f),
                    (byte)MathHelper.Clamp(lit.Y * 255f, 0f, 255f),
                    (byte)MathHelper.Clamp(lit.Z * 255f, 0f, 255f));
                SceneRasterizer.TriangleEdges edges = default(SceneRasterizer.TriangleEdges);
                edges.Setup(ref t, minY);
                double inverseArea = edges.InverseArea;
                for (int y = minY; y <= maxY; y++, edges.NextRow())
                {
                    int from;
                    int to;
                    double e0, e1, e2;
                    if (!edges.Span(t.MinX, t.MaxX, out from, out to, out e0, out e1, out e2))
                        continue;
                    int row = y * width;
                    for (int x = from; x <= to; x++)
                    {
                        if (e0 >= 0d && e1 >= 0d && e2 >= 0d)
                        {
                            float inverseZ = (float)(e0 * inverseArea) * t.InverseZ0 +
                                             (float)(e1 * inverseArea) * t.InverseZ1 +
                                             (float)(e2 * inverseArea) * t.InverseZ2;
                            if (inverseZ > 0f)
                            {
                                float z = -1f / inverseZ;
                                int index = row + x;
                                if (z > depthBuffer[index])
                                {
                                    if (solid)
                                        canvas.SetPixel(x, y, color);
                                    else
                                        canvas.BlendPixel(x, y, color, alpha);
                                    // A blended triangle writes depth too: the faces of the same model behind
                                    // it would otherwise show through one another in whatever order they came.
                                    depthBuffer[index] = z;
                                }
                            }
                        }

                        e0 += edges.Dx0;
                        e1 += edges.Dx1;
                        e2 += edges.Dx2;
                    }
                }
            };
            SceneRasterizer.ForEachInBand(_triangles, count, firstRow, endRow, raster);
        }
    }
}
