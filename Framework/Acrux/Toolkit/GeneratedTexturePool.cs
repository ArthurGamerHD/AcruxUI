using System;
using System.Collections.Generic;
using Acrux.Core;
using VRageMath;

namespace Acrux.Toolkit
{
    /// <summary>
    /// Creates generated textures on behalf of one mod. The game budgets generated textures per mod, so
    /// textures drawn for a client mod's UI are created through that mod's own delegate (its "sample"
    /// of <c>MyAPIGateway.GeneratedTextures</c>) instead of the toolkit's.
    /// </summary>
    internal sealed class GeneratedTexturePool
    {
        static readonly List<GeneratedTexturePool> Clients = new List<GeneratedTexturePool>();
        static int _version;

        internal static int Version => _version;

        readonly Func<string, Vector2I, bool, object> _create;
        readonly Func<bool> _isSupported;
        readonly Func<int> _maxTextureSide;
        readonly Func<object, bool> _ownsPeer;

        internal GeneratedTexturePool(
            string owner,
            Func<string, Vector2I, bool, object> create,
            Func<bool> isSupported,
            Func<int> maxTextureSide,
            Func<object, bool> ownsPeer)
        {
            Owner = owner ?? "client";
            _create = create;
            _isSupported = isSupported;
            _maxTextureSide = maxTextureSide;
            _ownsPeer = ownsPeer;
        }

        public string Owner { get; private set; }
        public bool IsSupported => _isSupported != null && _isSupported();
        public int MaxTextureSide => _maxTextureSide == null ? 0 : _maxTextureSide();

        // Returns the game's generated texture (as object, so this layer stays renderer-neutral) or null.
        public object Create(string nameHint, Vector2I size, bool linear)
        {
            return _create == null ? null : _create(nameHint, size, linear);
        }

        internal bool Owns(object peer)
        {
            try
            {
                return _ownsPeer != null && _ownsPeer(peer);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void Register(GeneratedTexturePool pool)
        {
            if (pool == null)
                return;
            lock (Clients)
            {
                Clients.Add(pool);
                _version++;
            }
        }

        internal static void Unregister(GeneratedTexturePool pool)
        {
            lock (Clients)
            {
                Clients.Remove(pool);
                _version++;
            }
        }

        internal static void Clear()
        {
            lock (Clients)
            {
                Clients.Clear();
                _version++;
            }
        }

        // The pool of the mod that created visual or its nearest client-created ancestor; null for UI that belongs to
        // the toolkit itself (the caller then uses the toolkit's own textures).
        internal static GeneratedTexturePool ForVisual(Visual visual)
        {
            if (visual != null && visual.ExternalPeer != null)
            {
                GeneratedTexturePool own = ForPeer(visual.ExternalPeer);
                if (own != null)
                    return own;
            }
            for (VisualElement current = visual as VisualElement; current != null;
                 current = current.Parent ?? current.OverlayOwner ?? (current as Acrux.Controls.Window)?.Owner)
            {
                object peer = current.ExternalPeer;
                if (peer == null)
                    continue;
                GeneratedTexturePool pool = ForPeer(peer);
                if (pool != null)
                    return pool;
            }
            return null;
        }

        internal static GeneratedTexturePool ForPeer(object peer)
        {
            if (peer == null)
                return null;
            lock (Clients)
            {
                for (int i = 0; i < Clients.Count; i++)
                {
                    if (Clients[i].Owns(peer))
                        return Clients[i];
                }
            }
            return null;
        }
    }

    // Renderers ask for the pool on every bordered element they draw; resolving it walks the visual's ancestors and
    // calls each client's ownership delegate across the bridge, so the result is kept until the root or the set of
    // registered pools changes. A null result is not kept: the root's peer may only be attached after the first draw.
    internal sealed class GeneratedTexturePoolCache
    {
        Visual _visual;
        GeneratedTexturePool _pool;
        int _version;

        internal GeneratedTexturePool ForVisual(Visual visual)
        {
            int version = GeneratedTexturePool.Version;
            GeneratedTexturePool pool = _pool;
            if (pool != null && ReferenceEquals(_visual, visual) && _version == version)
                return pool;

            pool = GeneratedTexturePool.ForVisual(visual);
            _visual = visual;
            _version = version;
            _pool = pool;
            return pool;
        }
    }
}
