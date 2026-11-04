using VRageMath;

namespace Acrux.Render3D.Voxels
{
    /// <summary>
    /// The color of each voxel material by its index, provided by the host: the voxel layer only draws, it does
    /// not decide how a material's color is found.
    /// </summary>
    internal interface IVoxelMaterialColors
    {
        // Game thread.
        void EnsureStarted();

        // Any thread; 0..1.
        Vector3 Get(byte materialIndex);

        bool IsLoading { get; }
    }
}
