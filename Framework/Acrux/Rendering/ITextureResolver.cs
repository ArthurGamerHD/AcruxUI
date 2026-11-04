using VRageMath;

namespace Acrux.Rendering
{
    public interface ITextureResolver
    {
        TextureSource MissingTexture { get; }
        bool TryResolve(TextureSource source, out object backendTexture);
        bool IsAvailable(TextureSource source);
        bool TryGetSize(TextureSource source, out Vector2 size);
    }
}
