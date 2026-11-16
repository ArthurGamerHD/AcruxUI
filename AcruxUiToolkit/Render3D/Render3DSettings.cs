using System;

namespace Acrux.Render3D
{
    /// <summary>
    /// Budgets of the 3D scene, per screen and frame. When a budget runs out, what is nearest the camera is
    /// kept and the farthest content is left out (blocks fall back to boxes first).
    /// </summary>
    public sealed class Render3DSettings
    {
        /// <summary>Triangles of block models, armor and subparts.</summary>
        public int BlockTriangles = 300000;

        /// <summary>Drawn block pieces (a model, an armor plate, a subpart or a box each).</summary>
        public int BlockInstances = 50000;

        /// <summary>Block models parsed from content files per frame; the rest show as boxes until loaded.</summary>
        public int BlockModelLoadsPerFrame = 6;

        /// <summary>Blocks smaller than this radius on screen, in pixels, are skipped.</summary>
        public double MinimumBlockPixels = 0.5d;

        /// <summary>Triangles of voxel maps (asteroids, boulders). Over it, voxel detail drops evenly.</summary>
        public int VoxelTriangles = 120000;

        /// <summary>Background tasks reading and meshing voxels at once.</summary>
        public int VoxelJobs = 3;

        /// <summary>Screen pixels per voxel mesh cell: higher is coarser and faster.</summary>
        public double VoxelPixelsPerCell = 4d;

        /// <summary>Threads painting frames at once, shared by every renderer; 0 uses half the hardware threads.</summary>
        public int PaintThreads = 0;

        internal static Render3DSettings Current
        {
            get { return Acrux.Bridge.AcruxSettings.Current.Render3D; }
        }

        internal void Clamp()
        {
            BlockTriangles = Math.Max(0, BlockTriangles);
            BlockInstances = Math.Max(0, BlockInstances);
            BlockModelLoadsPerFrame = Math.Max(1, BlockModelLoadsPerFrame);
            MinimumBlockPixels = Math.Max(0d, MinimumBlockPixels);
            VoxelTriangles = Math.Max(0, VoxelTriangles);
            VoxelJobs = Math.Max(1, Math.Min(16, VoxelJobs));
            VoxelPixelsPerCell = Math.Max(0.5d, VoxelPixelsPerCell);
            PaintThreads = Math.Max(0, Math.Min(256, PaintThreads));
        }
    }
}
