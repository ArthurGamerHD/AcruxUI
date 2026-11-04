using System;
using Acrux.Render3D;
using Sandbox.ModAPI;
using VRageMath;

namespace Acrux.Cartography.Maps.Planet3D
{
    /// <summary>
    /// Per-pixel terrain attributes between rasterization and shading. A pixel belongs to the current frame
    /// when its stamp matches, so the arrays are never cleared.
    /// </summary>
    internal sealed class TerrainPixelBuffer
    {
        public Vector3[] Directions = new Vector3[0];
        public float[] Shades = new float[0];
        public byte[] Mips = new byte[0];
        public int[] Stamps = new int[0];
        public int Stamp;

        public void Prepare(int pixelCount)
        {
            if (Stamps.Length < pixelCount)
            {
                Directions = new Vector3[pixelCount];
                Shades = new float[pixelCount];
                Mips = new byte[pixelCount];
                Stamps = new int[pixelCount];
                Stamp = 0;
            }
            if (Stamp == int.MaxValue)
            {
                Array.Clear(Stamps, 0, Stamps.Length);
                Stamp = 0;
            }
            Stamp++;
        }
    }

    /// <summary>The terrain's half of the rasterizer: G-buffer writes and cubemap mip selection.</summary>
    internal static class TerrainRasterizer
    {
        // Shading happens afterwards, once per visible pixel (TerrainScene), not once per overdrawing triangle.
        public static void RasterizeTerrain(
            int width,
            int height,
            float[] depthBuffer,
            SceneTriangleList triangles,
            TerrainPixelBuffer pixels,
            int firstRow,
            int endRow)
        {
            ScreenTriangle[] items = triangles.Items;
            int count = triangles.Count;
            int lastRow = Math.Min(height, endRow) - 1;
            Vector3[] directions = pixels.Directions;
            float[] shades = pixels.Shades;
            byte[] mips = pixels.Mips;
            int[] stamps = pixels.Stamps;
            int stamp = pixels.Stamp;
            Action<int> raster = i =>
            {
                int minY = Math.Max(items[i].MinY, firstRow);
                int maxY = Math.Min(items[i].MaxY, lastRow);
                if (minY > maxY)
                    return;

                ScreenTriangle t = items[i];
                byte mip = (byte)MathHelper.Clamp(t.Mip, 0, 255);
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
                                    directions[index] = (t.A0 * w0 + t.A1 * w1 + t.A2 * w2) * z;
                                    shades[index] = (t.S0 * w0 + t.S1 * w1 + t.S2 * w2) * z;
                                    mips[index] = mip;
                                    stamps[index] = stamp;
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

        // For choosing a texture mip.
        public static void SetMip(SceneTriangleList triangles, int firstTriangle, PlanetColorCubemap cubemap)
        {
            ScreenTriangle[] items = triangles.Items;
            for (int i = firstTriangle; i < triangles.Count; i++)
            {
                ScreenTriangle t = items[i];
                double pixels = Math.Sqrt(Math.Max(
                    Squared(t.X1 - t.X0, t.Y1 - t.Y0),
                    Math.Max(Squared(t.X2 - t.X1, t.Y2 - t.Y1), Squared(t.X0 - t.X2, t.Y0 - t.Y2))));
                // The attribute is a unit direction: a chord between two of them is their angle in radians.
                Vector3 d0 = t.A0 / t.InverseZ0;
                Vector3 d1 = t.A1 / t.InverseZ1;
                Vector3 d2 = t.A2 / t.InverseZ2;
                double arc = Math.Sqrt(Math.Max(
                    Vector3.DistanceSquared(d0, d1),
                    Math.Max(Vector3.DistanceSquared(d1, d2), Vector3.DistanceSquared(d2, d0))));
                // One cube face spans a quarter turn.
                double faceSamples = arc <= 1e-9
                    ? 1e9
                    : Math.Max(1d, pixels) * MathHelper.PiOver2 / arc;
                items[i].Mip = cubemap.SelectMipLevel((float)Math.Min(1e9, faceSamples));
            }
        }

        static double Squared(double x, double y)
        {
            return x * x + y * y;
        }
    }
}
