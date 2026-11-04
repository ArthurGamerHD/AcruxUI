using System;
using System.Collections.Generic;
using Acrux.Rendering;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Lcd
{
    /// <summary>Resolves renderer-neutral texture keys to LCD sprite names.</summary>
    public sealed class LcdTextureResolver : ITextureResolver, global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly HashSet<string> _registeredSprites =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly TextureSource _missingTexture;

        public LcdTextureResolver(IMyTextSurface surface, string missingTexture = "MissingIcon")
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));

            _missingTexture = TextureSource.FromKey(missingTexture);
            var sprites = new List<string>();
            surface.GetSprites(sprites);
            for (int i = 0; i < sprites.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(sprites[i]))
                    _registeredSprites.Add(sprites[i]);
            }
        }

        /// <summary>
        /// A surface lists only the LCD textures defined when it was created, but textures defined later (block
        /// icons, imported textures) still render. Hosts set this to look a texture definition up by name.
        /// </summary>
        public static Func<string, bool> IsTextureDefined { get; set; }

        /// <summary>Hosts set this to look up an LCD texture's pixel size; zero when unknown.</summary>
        public static Func<string, Vector2> TextureSize { get; set; }

        bool IsRegistered(string name)
        {
            if (_registeredSprites.Contains(name))
                return true;
            Func<string, bool> defined = IsTextureDefined;
            if (defined == null || !defined(name))
                return false;
            _registeredSprites.Add(name);
            return true;
        }

        public TextureSource MissingTexture => _missingTexture;

        public bool TryResolve(TextureSource source, out object backendTexture)
        {
            IMyGeneratedTexture generatedTexture = source.NativeHandle as IMyGeneratedTexture;
            if (generatedTexture != null && generatedTexture.IsValid)
            {
                backendTexture = generatedTexture.Name;
                return !string.IsNullOrWhiteSpace((string)backendTexture);
            }

            string nativeName = source.NativeHandle as string;
            if (!string.IsNullOrWhiteSpace(nativeName))
            {
                backendTexture = nativeName;
                return true;
            }

            if (source.NativeHandle is MyStringId)
            {
                string materialName = ((MyStringId)source.NativeHandle).String;
                backendTexture = materialName;
                return !string.IsNullOrWhiteSpace(materialName);
            }

            backendTexture = source.Key;
            return !string.IsNullOrWhiteSpace(source.Key);
        }

        public bool IsAvailable(TextureSource source)
        {
            if (source.NativeHandle is IMyGeneratedTexture)
                return ((IMyGeneratedTexture)source.NativeHandle).IsValid;

            object resolved;
            if (!TryResolve(source, out resolved))
                return false;

            string name = resolved as string;
            return !string.IsNullOrWhiteSpace(name) &&
                   (IsRegistered(name) || IsGeneratedTextureName(source));
        }

        public bool TryGetSize(TextureSource source, out Vector2 size)
        {
            size = Vector2.Zero;
            Func<string, Vector2> lookup = TextureSize;
            object resolved;
            string name = lookup != null && TryResolve(source, out resolved) ? resolved as string : null;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            size = lookup(name);
            return size.X > 0f && size.Y > 0f;
        }

        static bool IsGeneratedTextureName(TextureSource source)
        {
            return source.NativeHandle is string || source.NativeHandle is IMyGeneratedTexture;
        }
    }
}
