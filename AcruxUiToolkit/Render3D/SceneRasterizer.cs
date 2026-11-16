using System;
using Acrux.Rendering;
using Sandbox.ModAPI;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// The software rasterizer: edge-function coverage, a depth buffer and perspective-correct interpolation.
    /// Depth values follow the map renderer's convention: Space Engineers camera-space Z in metres, negative
    /// in front of the camera, so a larger value is nearer and an untouched pixel is negative infinity.
    /// </summary>
    internal static class SceneRasterizer
    {
        // The attribute is each vertex's lit color in 0..1, interpolated perspective-correctly.
        public static void DrawGouraud(
            PixelCanvas canvas,
            float[] depthBuffer,
            SceneTriangleList triangles,
            int firstRow,
            int endRow)
        {
            ScreenTriangle[] items = triangles.Items;
            int count = triangles.Count;
            int width = canvas.Width;
            int lastRow = Math.Min(canvas.Height, endRow) - 1;
            Action<int> raster = i =>
            {
                int minY = Math.Max(items[i].MinY, firstRow);
                int maxY = Math.Min(items[i].MaxY, lastRow);
                if (minY > maxY)
                    return;

                ScreenTriangle t = items[i];
                TriangleEdges edges = default(TriangleEdges);
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
                            float w0 = (float)(e0 * inverseArea);
                            float w1 = (float)(e1 * inverseArea);
                            float w2 = (float)(e2 * inverseArea);
                            float inverseZ = w0 * t.InverseZ0 + w1 * t.InverseZ1 + w2 * t.InverseZ2;
                            if (inverseZ > 0f)
                            {
                                float z = 1f / inverseZ;
                                int index = row + x;
                                if (-z > depthBuffer[index])
                                {
                                    Vector3 lit = (t.A0 * w0 + t.A1 * w1 + t.A2 * w2) * z;
                                    canvas.SetPixel(x, y, new Color(
                                        (byte)MathHelper.Clamp(lit.X * 255f, 0f, 255f),
                                        (byte)MathHelper.Clamp(lit.Y * 255f, 0f, 255f),
                                        (byte)MathHelper.Clamp(lit.Z * 255f, 0f, 255f)));
                                    depthBuffer[index] = -z;
                                }
                            }
                        }

                        e0 += edges.Dx0;
                        e1 += edges.Dx1;
                        e2 += edges.Dx2;
                    }
                }
            };
            SceneRasterizer.ForEachInBand(triangles, count, firstRow, endRow, raster);
        }

        // The attribute is the triangle's lit color in 0..1.
        public static void DrawFlat(
            PixelCanvas canvas,
            float[] depthBuffer,
            SceneTriangleList triangles,
            int firstRow,
            int endRow)
        {
            ScreenTriangle[] items = triangles.Items;
            int count = triangles.Count;
            int width = canvas.Width;
            int lastRow = Math.Min(canvas.Height, endRow) - 1;
            Action<int> raster = i =>
            {
                int minY = Math.Max(items[i].MinY, firstRow);
                int maxY = Math.Min(items[i].MaxY, lastRow);
                if (minY > maxY)
                    return;

                ScreenTriangle t = items[i];
                Vector3 lit = t.A0 / t.InverseZ0;
                var color = new Color(
                    (byte)MathHelper.Clamp(lit.X * 255f, 0f, 255f),
                    (byte)MathHelper.Clamp(lit.Y * 255f, 0f, 255f),
                    (byte)MathHelper.Clamp(lit.Z * 255f, 0f, 255f));
                TriangleEdges edges = default(TriangleEdges);
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
                                    canvas.SetPixel(x, y, color);
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
            SceneRasterizer.ForEachInBand(triangles, count, firstRow, endRow, raster);
        }

        // Binned: only the band's own triangles, in order on its worker (bands already run in parallel, and depth
        // writes within a band never race). Else every triangle, in parallel.
        internal static void ForEachInBand(SceneTriangleList triangles, int count, int firstRow, int endRow,
            Action<int> raster)
        {
            int start;
            int end;
            if (triangles.TryGetBand(firstRow, endRow, out start, out end))
            {
                int[] items = triangles.BandItems;
                for (int k = start; k < end; k++)
                    raster(items[k]);
                return;
            }
            PaintWorkers.For(0, count, raster);
        }

        /// <summary>
        /// The three edge functions of a triangle, stepped one pixel at a time: oriented once so a pixel centre
        /// is inside when all three are non-negative whatever the winding, and narrowed per row to the span the
        /// triangle can cover, so a row never walks the empty part of the bounding box.
        /// </summary>
        internal struct TriangleEdges
        {
            // At pixel (MinX, current row): the centre (MinX + 0.5, row + 0.5).
            public double E0, E1, E2;
            public double Dx0, Dx1, Dx2;
            public double Dy0, Dy1, Dy2;
            // Signed like the edges, so e * InverseArea is the barycentric weight.
            public double InverseArea;

            public void Setup(ref ScreenTriangle t, int firstRow)
            {
                double x0 = t.X0, y0 = t.Y0, x1 = t.X1, y1 = t.Y1, x2 = t.X2, y2 = t.Y2;
                double area = Edge(x0, y0, x1, y1, x2, y2);
                double sign = area > 0d ? 1d : -1d;
                InverseArea = sign / area;
                Dx0 = sign * (y2 - y1);
                Dx1 = sign * (y0 - y2);
                Dx2 = sign * (y1 - y0);
                Dy0 = -sign * (x2 - x1);
                Dy1 = -sign * (x0 - x2);
                Dy2 = -sign * (x1 - x0);
                double startX = t.MinX + 0.5d;
                double startY = firstRow + 0.5d;
                E0 = sign * Edge(x1, y1, x2, y2, startX, startY);
                E1 = sign * Edge(x2, y2, x0, y0, startX, startY);
                E2 = sign * Edge(x0, y0, x1, y1, startX, startY);
            }

            public void NextRow()
            {
                E0 += Dy0;
                E1 += Dy1;
                E2 += Dy2;
            }

            // A pixel of margin either side; the caller still tests each one.
            public bool Span(int minX, int maxX, out int from, out int to, out double e0, out double e1,
                out double e2)
            {
                from = minX;
                to = maxX;
                Clip(E0, Dx0, minX, ref from, ref to);
                Clip(E1, Dx1, minX, ref from, ref to);
                Clip(E2, Dx2, minX, ref from, ref to);
                int steps = from - minX;
                e0 = E0 + steps * Dx0;
                e1 = E1 + steps * Dx1;
                e2 = E2 + steps * Dx2;
                return from <= to;
            }

            static void Clip(double e, double dx, int origin, ref int from, ref int to)
            {
                if (dx > 0d)
                {
                    // Inside from x = origin - e / dx on.
                    double x = origin - e / dx;
                    if (x > to + 1d)
                        to = from - 1;
                    else if (x - 1d > from)
                        from = (int)(x - 1d);
                }
                else if (dx < 0d)
                {
                    // Inside up to x = origin - e / dx.
                    double x = origin - e / dx;
                    if (x < from - 1d)
                        to = from - 1;
                    else if (x + 1d < to)
                        to = (int)(x + 1d);
                }
                else if (e < 0d)
                {
                    to = from - 1;
                }
            }
        }

        internal static double Edge(double ax, double ay, double bx, double by, double px, double py)
        {
            return (px - ax) * (by - ay) - (py - ay) * (bx - ax);
        }

        internal static Color Shade(Color color, float shade)
        {
            if (shade == 1f)
                return color;
            return new Color(
                (byte)MathHelper.Clamp(color.R * shade, 0f, 255f),
                (byte)MathHelper.Clamp(color.G * shade, 0f, 255f),
                (byte)MathHelper.Clamp(color.B * shade, 0f, 255f),
                color.A);
        }

        internal static Color Tint(Color background, Color tint, byte coverage)
        {
            int alpha = (coverage * tint.A + 127) / 255;
            int inverse = 255 - alpha;
            return new Color(
                (byte)((tint.R * alpha + background.R * inverse + 127) / 255),
                (byte)((tint.G * alpha + background.G * inverse + 127) / 255),
                (byte)((tint.B * alpha + background.B * inverse + 127) / 255),
                background.A);
        }
    }
}
