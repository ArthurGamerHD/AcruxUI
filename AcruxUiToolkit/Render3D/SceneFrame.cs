using System;
using System.Collections.Generic;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// Captured raster work. Commands read only captured values and immutable data (cubemaps, cached models);
    /// each worker owns a disjoint range of rows, including the matching depth values.
    /// </summary>
    internal sealed class SceneFrame
    {
        public readonly List<Action<PixelCanvas, int, int>> Commands =
            new List<Action<PixelCanvas, int, int>>();
        public Action Initialize;
        // Independent of each other: they run in parallel after Initialize, before any band draws.
        public readonly List<Action> Builds = new List<Action>();
        public Action<PixelCanvas> Finish;
        public float[] DepthBuffer;

        // Depths are camera-space Z: larger is nearer, so the far value is negative infinity.
        public static float[] ClearDepth(ref float[] buffer, int width, int height)
        {
            int length = Math.Max(0, width * height);
            if (buffer == null || buffer.Length != length)
                buffer = new float[length];
            float[] depth = buffer;
            // Doubling block copies: memory-speed instead of a per-element loop on the paint coordinator.
            int count = depth.Length;
            if (count > 0)
            {
                depth[0] = float.NegativeInfinity;
                for (int filled = 1; filled < count; filled *= 2)
                    Array.Copy(depth, 0, depth, filled, Math.Min(filled, count - filled));
            }
            return depth;
        }
        // One slot per band, written only by that band's worker: failures are collected without locks.
        Exception[] _failures = new Exception[0];

        public void Paint(PixelCanvas canvas, Action<int, int, Action<int>> parallelFor)
        {
            canvas.Clear(Color.Transparent);
            if (Initialize != null)
                Initialize();
            BuildLayers(parallelFor);

            // More bands than workers lets the game scheduler balance cheap rows against expensive ones
            // without creating threads per frame. The 3D layers bin their triangles by this band height.
            const int rowsPerBand = SceneTriangleList.BAND_ROWS;
            int bands = (canvas.Height + rowsPerBand - 1) / rowsPerBand;
            if (_failures.Length < bands)
                _failures = new Exception[bands];
            else
                Array.Clear(_failures, 0, bands);
            Exception[] failures = _failures;
            parallelFor(0, bands, band =>
            {
                try
                {
                    int first = band * rowsPerBand;
                    int end = Math.Min(canvas.Height, first + rowsPerBand);
                    for (int i = 0; i < Commands.Count; i++)
                        Commands[i](canvas, first, end);
                }
                catch (Exception exception)
                {
                    // Catch inside the worker: the engine may be configured to
                    // treat uncaught parallel task exceptions as fatal.
                    failures[band] = exception;
                }
            });
            for (int band = 0; band < bands; band++)
            {
                if (failures[band] != null)
                    throw new Exception("Scene rasterization failed.", failures[band]);
            }

            // After every band's depth writes (the map's orbit lines merge strokes against all of them), still on
            // the background coordinator.
            if (Finish != null)
                Finish(canvas);
        }

        void BuildLayers(Action<int, int, Action<int>> parallelFor)
        {
            int count = Builds.Count;
            if (count == 0)
                return;
            if (count == 1)
            {
                Builds[0]();
                return;
            }
            // Built one after another these were a third of the map's frame (blocks, voxels and terrain each
            // take milliseconds) while every paint worker idled.
            var failures = new Exception[count];
            parallelFor(0, count, i =>
            {
                try
                {
                    Builds[i]();
                }
                catch (Exception exception)
                {
                    failures[i] = exception;
                }
            });
            for (int i = 0; i < count; i++)
            {
                if (failures[i] != null)
                    throw new Exception("Scene build failed.", failures[i]);
            }
        }
    }
}
