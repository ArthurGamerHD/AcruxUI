using Acrux.Rendering;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace Acrux.ScreenSpace
{
    public sealed class ScreenSpaceTextureResolver : ITextureResolver
    {
        public TextureSource MissingTexture => TextureSource.FromKey("Minimap_MissingIcon");

        public bool TryResolve(TextureSource source, out object backendTexture)
        {
            // Generated textures draw by material here and by name on LCDs, so
            // shared controls pass the texture itself.
            IMyGeneratedTexture generated = source.NativeHandle as IMyGeneratedTexture;
            if (generated != null)
            {
                backendTexture = generated.MaterialId;
                return generated.IsValid;
            }

            if (source.NativeHandle is MyStringId)
            {
                backendTexture = (MyStringId)source.NativeHandle;
                return (MyStringId)backendTexture != MyStringId.NullOrEmpty;
            }

            if (!string.IsNullOrWhiteSpace(source.Key))
            {
                backendTexture = MyStringId.GetOrCompute(source.Key);
                return true;
            }

            backendTexture = null;
            return false;
        }

        public bool IsAvailable(TextureSource source)
        {
            object resolved;
            return TryResolve(source, out resolved) &&
                   MyScreenSpaceSprite.IsMaterialAvailable((MyStringId)resolved);
        }

        public bool TryGetSize(TextureSource source, out Vector2 size)
        {
            size = Vector2.Zero;
            return false;
        }
    }
}
