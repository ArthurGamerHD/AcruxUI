using System;
using Acrux.Rendering;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace Acrux.ScreenSpace
{
    /// <summary>
    /// Owns one ModAPI-generated DX12 texture and its renderer-managed upload buffers.
    /// </summary>
    public sealed class DynamicMapTexture : IDynamicTexture, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        IMyGeneratedTexture _texture;

        public DynamicMapTexture(string name, Vector2I requestedSize)
            : this(name, requestedSize, null)
        {
        }

        /// <param name="pool">Mod whose texture budget pays for this texture; null for the toolkit's own.</param>
        internal DynamicMapTexture(string name, Vector2I requestedSize, global::Acrux.Toolkit.GeneratedTexturePool pool)
        {
            Vector2I size = FitToRenderer(requestedSize, pool);
            if (size.X <= 0 || size.Y <= 0)
                return;

            if (pool != null)
            {
                _texture = pool.Create(name, size, false) as IMyGeneratedTexture;
                return;
            }
            IMyGeneratedTextures textures = MyAPIGateway.GeneratedTextures;
            if (textures != null)
                _texture = textures.Create(name, size, MyGeneratedTextureFormat.Srgb);
        }

        public IMyGeneratedTexture Texture => _texture;
        public TextureSource Source => IsAvailable
            ? TextureSource.FromNative(_texture.MaterialId)
            : default(TextureSource);
        public bool IsAvailable => _texture != null && _texture.IsValid;
        public Vector2I Size => IsAvailable ? _texture.Size : Vector2I.Zero;

        public static Vector2I FitToRenderer(Vector2I requestedSize)
        {
            return FitToRenderer(requestedSize, null);
        }

        internal static Vector2I FitToRenderer(Vector2I requestedSize, global::Acrux.Toolkit.GeneratedTexturePool pool)
        {
            bool supported;
            int maxSide;
            if (pool != null)
            {
                supported = pool.IsSupported;
                maxSide = pool.MaxTextureSide;
            }
            else
            {
                IMyGeneratedTextures textures = MyAPIGateway.GeneratedTextures;
                supported = textures != null && textures.IsSupported;
                maxSide = textures == null ? 0 : textures.MaxTextureSide;
            }
            if (!supported)
                return Vector2I.Zero;
            return DynamicTextureSize.Fit(requestedSize, maxSide);
        }

        public bool Update(Action<PixelCanvas> paint)
        {
            if (!IsAvailable || paint == null)
                return false;

            byte[] pixels = _texture.BeginUpdate();
            if (pixels == null)
                return false;

            try
            {
                paint(new PixelCanvas(pixels, _texture.Size.X, _texture.Size.Y));
            }
            finally
            {
                _texture.EndUpdate(pixels);
            }
            return true;
        }

        // Pixel generation may run on a worker; generated-texture access stays on
        // the simulation/UI thread that calls this method.
        public bool UploadPixels(byte[] source)
        {
            if (!IsAvailable || source == null ||
                source.Length != _texture.Size.X * _texture.Size.Y * 4)
                return false;

            byte[] pixels = _texture.BeginUpdate();
            if (pixels == null)
                return false;
            try
            {
                Buffer.BlockCopy(source, 0, pixels, 0, source.Length);
            }
            finally
            {
                _texture.EndUpdate(pixels);
            }
            return true;
        }

        public void Dispose()
        {
            if (_texture == null)
                return;
            _texture.Dispose();
            _texture = null;
        }
    }

    public sealed class ScreenSpaceDynamicTextureFactory : IDynamicTextureFactory, ISharedTextureFactory,
        global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly Func<global::Acrux.Toolkit.GeneratedTexturePool> _pool;

        public ScreenSpaceDynamicTextureFactory()
        {
        }

        /// <param name="pool">Resolves the mod that pays for the textures, when they are created.</param>
        internal ScreenSpaceDynamicTextureFactory(Func<global::Acrux.Toolkit.GeneratedTexturePool> pool)
        {
            _pool = pool;
        }

        global::Acrux.Toolkit.GeneratedTexturePool Pool => _pool == null ? null : _pool();

        global::Acrux.Toolkit.GeneratedTexturePool ISharedTextureFactory.SharingScope => Pool;

        public Vector2I FitToSupportedSize(Vector2I requestedSize)
        {
            return DynamicMapTexture.FitToRenderer(requestedSize, Pool);
        }

        public IDynamicTexture Create(string name, Vector2I requestedSize)
        {
            return new DynamicMapTexture(name, requestedSize, Pool);
        }
    }
}