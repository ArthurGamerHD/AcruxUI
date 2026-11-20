using System;
using System.Collections.Generic;
using Acrux.Toolkit;

namespace Acrux.Rendering
{
    // A factory whose textures any surface of its kind can draw (LCD textures by name, screen-space ones by material),
    // paid for by the mod its scope names. Surfaces with the same kind and scope share their generated textures.
    internal interface ISharedTextureFactory
    {
        // The pool of the mod that pays for the textures; null for the toolkit's own budget.
        GeneratedTexturePool SharingScope { get; }
    }

    // One texture store per kind of surface and paying mod, shared by every surface of that kind: two LCDs drawing
    // the same rounded Border or gradient use one texture. Stores never keep a factory (it holds its renderer and
    // visual tree); textures are made through the factory of the surface asking.
    internal static class SharedTextureStores
    {
        struct Scope : IEquatable<Scope>
        {
            public Type Kind;
            public GeneratedTexturePool Pool;

            public bool Equals(Scope other)
            {
                return Kind == other.Kind && ReferenceEquals(Pool, other.Pool);
            }

            public override bool Equals(object obj)
            {
                return obj is Scope && Equals((Scope)obj);
            }

            public override int GetHashCode()
            {
                return (Kind.GetHashCode() * 397) ^ (Pool == null ? 0 : Pool.GetHashCode());
            }
        }

        sealed class Stores
        {
            public BorderTextureStore Borders = new BorderTextureStore();
            public GradientTextureCache Gradients = new GradientTextureCache();
        }

        // Read by every Border of every surface each frame: published without a lock, replaced under the writer lock.
        static volatile Dictionary<Scope, Stores> _active = new Dictionary<Scope, Stores>();
        static readonly object Writer = new object();
        static volatile int _poolVersion = -1;

        // Null when the factory's textures cannot be shared between surfaces.
        internal static BorderTextureStore Border(IDynamicTextureFactory factory)
        {
            Stores stores = For(factory);
            return stores == null ? null : stores.Borders;
        }

        internal static GradientTextureCache Gradient(IDynamicTextureFactory factory)
        {
            Stores stores = For(factory);
            return stores == null ? null : stores.Gradients;
        }

        static Stores For(IDynamicTextureFactory factory)
        {
            var shared = factory as ISharedTextureFactory;
            if (shared == null)
                return null;
            var scope = new Scope { Kind = factory.GetType(), Pool = shared.SharingScope };
            Stores stores;
            if (GeneratedTexturePool.Version == _poolVersion && _active.TryGetValue(scope, out stores))
                return stores;
            lock (Writer)
            {
                int version = GeneratedTexturePool.Version;
                var next = Prune(version);
                if (!next.TryGetValue(scope, out stores))
                {
                    if (ReferenceEquals(next, _active))
                        next = new Dictionary<Scope, Stores>(_active);
                    next.Add(scope, stores = new Stores());
                }
                // The table first: a reader that sees the new version must also see the pruned table.
                _active = next;
                _poolVersion = version;
                return stores;
            }
        }

        internal static void Clear()
        {
            lock (Writer)
            {
                foreach (Stores stores in _active.Values)
                {
                    stores.Borders.Clear();
                    stores.Gradients.Clear();
                }
                _active = new Dictionary<Scope, Stores>();
                _poolVersion = -1;
            }
        }

        // A client that disconnected takes its pool with it: its stores go, and with them its textures. Returns the
        // current table when nothing went.
        static Dictionary<Scope, Stores> Prune(int version)
        {
            Dictionary<Scope, Stores> current = _active;
            if (version == _poolVersion)
                return current;
            Dictionary<Scope, Stores> next = null;
            foreach (var pair in current)
            {
                if (pair.Key.Pool == null || GeneratedTexturePool.IsRegistered(pair.Key.Pool))
                    continue;
                if (next == null)
                    next = new Dictionary<Scope, Stores>(current);
                next.Remove(pair.Key);
                pair.Value.Borders.Clear();
                pair.Value.Gradients.Clear();
            }
            return next ?? current;
        }
    }
}
