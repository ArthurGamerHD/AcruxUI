using System.Collections.Generic;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// One optional kind of content in a 3D scene (a planet's terrain, its atmosphere, grids, voxel maps). A host
    /// adds only the layers it wants; layers never reference each other and meet only in the shared depth
    /// buffer, so any of them can be left out.
    /// <para>
    /// Lifecycle per frame: the layer captures what it needs on the game thread (its own Capture method, with
    /// whatever inputs that kind of content takes), then <see cref="Build"/> runs once on the paint coordinator,
    /// then <see cref="Draw"/> runs on every row band, in the order the layers were added.
    /// </para>
    /// </summary>
    internal interface ISceneLayer
    {
        // Paint coordinator, once per frame before any band draws.
        void Build();

        // Row-band worker.
        void Draw(PixelCanvas canvas, float[] depthBuffer, int firstRow, int endRow);

        // Background work the last capture left (shown as loading, kept animating); null when complete.
        string PendingWork { get; }
    }

    /// <summary>
    /// The common inputs of layers that draw world entities: the camera in scene space, the world-to-scene
    /// transform, the region to gather from, and the light.
    /// </summary>
    internal struct SceneCapture
    {
        public SceneCamera Camera;
        public MatrixD WorldToScene;
        public Vector3D CenterWorld;
        public double Radius;
        // Toward the light, in scene space.
        public Vector3 Light;
        // Its own block is left out (the screen a first-person camera looks out of).
        public Vector3D? ExcludeWorld;
        // When set, only the entities with these ids are gathered (grids, voxel maps and characters by their own
        // ids; planets' vegetation and forageables draw anyway, as their terrain does).
        public HashSet<long> Only;
    }

    /// <summary>Process-wide caches of the 3D engine's layers, released with the session.</summary>
    internal static class Render3DModule
    {
        public static void ReleaseShared()
        {
            Blocks.BlockLayer.ReleaseShared();
            Voxels.VoxelLayer.ReleaseShared();
            Models.MeshAssets.Release();
        }
    }
}
