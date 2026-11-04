using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Acrux.Rendering;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace Acrux.Lcd
{
    /// <summary>
    /// Owns a generated texture exposed to LCD sprites by its registered texture name.
    /// </summary>
    public sealed class LcdDynamicTexture : IDynamicTexture, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        // Read for every texture sprite an upload compares, also from screens rendering in parallel.
        static readonly ConcurrentDictionary<string, int> ContentVersions = new ConcurrentDictionary<string, int>();
        static long _globalContentVersion;
        IMyGeneratedTexture _texture;

        /// <summary>Incremented whenever any generated texture's pixels change.</summary>
        public static long GlobalContentVersion => Interlocked.Read(ref _globalContentVersion);

        /// <summary>Pixel version of the generated texture registered as <paramref name="name"/>; 0 for other textures.</summary>
        /// <summary>Records a pixel change for a generated texture updated outside this wrapper.</summary>
        public static void NotifyContentChanged(string name)
        {
            if (name == null)
                return;
            ContentVersions.AddOrUpdate(name, 1, (key, version) => version + 1);
            Interlocked.Increment(ref _globalContentVersion);
        }

        /// <summary>Forgets the version of a generated texture disposed outside this wrapper.</summary>
        public static void NotifyDisposed(string name)
        {
            int removed;
            if (name != null)
                ContentVersions.TryRemove(name, out removed);
        }

        public static int GetContentVersion(string name)
        {
            if (name == null)
                return 0;
            int version;
            return ContentVersions.TryGetValue(name, out version) ? version : 0;
        }

        public LcdDynamicTexture(string name, Vector2I requestedSize)
            : this(name, requestedSize, null)
        {
        }

        /// <param name="pool">Mod whose texture budget pays for this texture; null for the toolkit's own.</param>
        internal LcdDynamicTexture(string name, Vector2I requestedSize, global::Acrux.Toolkit.GeneratedTexturePool pool)
        {
            Vector2I size = FitToRenderer(requestedSize, pool);
            if (size.X <= 0 || size.Y <= 0)
                return;

            _texture = CreateTexture(name, size, pool);
        }

        internal static IMyGeneratedTexture CreateTexture(string name, Vector2I size, global::Acrux.Toolkit.GeneratedTexturePool pool)
        {
            if (pool != null)
                return pool.Create(name, size, false) as IMyGeneratedTexture;
            IMyGeneratedTextures textures = MyAPIGateway.GeneratedTextures;
            return textures == null ? null : textures.Create(name, size, MyGeneratedTextureFormat.Srgb);
        }

        public TextureSource Source => IsAvailable
            ? TextureSource.FromNative(_texture)
            : default(TextureSource);
        public bool IsAvailable => _texture != null && _texture.IsValid;
        public string Name => IsAvailable ? _texture.Name : null;
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
            if (!supported || maxSide <= 0 || requestedSize.X <= 0 || requestedSize.Y <= 0)
                return Vector2I.Zero;

            int largestRequestedSide = Math.Max(requestedSize.X, requestedSize.Y);
            if (largestRequestedSide <= maxSide)
                return requestedSize;

            double scale = maxSide / (double)largestRequestedSide;
            return new Vector2I(
                Math.Max(1, (int)Math.Round(requestedSize.X * scale)),
                Math.Max(1, (int)Math.Round(requestedSize.Y * scale)));
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
            NotifyContentChanged(_texture.Name);
            return true;
        }

        public void Dispose()
        {
            if (_texture == null)
                return;
            int removed;
            ContentVersions.TryRemove(_texture.Name, out removed);
            _texture.Dispose();
            _texture = null;
        }
    }

    public sealed class LcdDynamicTextureFactory : IDynamicTextureFactory, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly Func<global::Acrux.Toolkit.GeneratedTexturePool> _pool;

        public LcdDynamicTextureFactory()
        {
        }

        /// <param name="pool">Resolves the mod that pays for the textures, when they are created.</param>
        internal LcdDynamicTextureFactory(Func<global::Acrux.Toolkit.GeneratedTexturePool> pool)
        {
            _pool = pool;
        }

        global::Acrux.Toolkit.GeneratedTexturePool Pool => _pool == null ? null : _pool();

        public Vector2I FitToSupportedSize(Vector2I requestedSize)
        {
            return LcdDynamicTexture.FitToRenderer(requestedSize, Pool);
        }

        public IDynamicTexture Create(string name, Vector2I requestedSize)
        {
            return new LcdDynamicTexture(name, requestedSize, Pool);
        }
    }
}
