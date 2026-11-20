using System;
using System.Collections.Generic;
using System.Threading;

namespace Acrux.Rendering
{
    // A shared texture and its users. -1 users means retired: swept and about to be disposed, so no one may take it.
    internal sealed class SharedTextureEntry
    {
        int _references;
        long _releasedTicks;

        internal SharedTextureEntry(IDynamicTexture texture, object createdIn, object storedKey)
        {
            Texture = texture;
            CreatedIn = createdIn;
            StoredKey = storedKey;
            _references = 1;
        }

        internal IDynamicTexture Texture { get; }
        // The key the entry is kept under, which may hold snapshots the lookup key did not.
        internal object StoredKey { get; }
        internal object CreatedIn { get; }

        internal bool TryRetain()
        {
            while (true)
            {
                int references = _references;
                if (references < 0)
                    return false;
                if (Interlocked.CompareExchange(ref _references, references + 1, references) == references)
                    return true;
            }
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                Interlocked.Exchange(ref _releasedTicks, DateTime.UtcNow.Ticks);
        }

        internal bool TryRetire(long now, long retention)
        {
            return _references == 0 && now - Interlocked.Read(ref _releasedTicks) > retention &&
                   Interlocked.CompareExchange(ref _references, -1, 0) == 0;
        }
    }

    // Textures shared by surfaces that can render in parallel. Every frame reads the published table without a lock;
    // a miss takes the writer lock, which only other writers wait on, builds the next table and publishes it. The
    // table read by a frame is never changed afterwards, so it is copied rather than reused: a frame on another thread
    // may still be reading the previous one.
    internal sealed class SharedTextureTable<TKey> where TKey : IEquatable<TKey>
    {
        static readonly long RetentionTicks = TimeSpan.TicksPerSecond * 10;
        static readonly long SweepIntervalTicks = TimeSpan.TicksPerSecond;

        volatile Dictionary<TKey, SharedTextureEntry> _active = new Dictionary<TKey, SharedTextureEntry>();
        readonly object _writer = new object();
        long _nextSweepTicks;

        // storedKey turns the lookup key into the one kept (a snapshot of anything the caller may still change);
        // create makes and paints the texture, or returns null when it cannot.
        internal SharedTextureEntry Acquire(TKey key, object frame, Func<TKey, TKey> storedKey,
            Func<IDynamicTexture> create, out bool created)
        {
            created = false;
            long now = DateTime.UtcNow.Ticks;
            if (now >= Interlocked.Read(ref _nextSweepTicks))
                Sweep(now);

            SharedTextureEntry entry;
            if (_active.TryGetValue(key, out entry) && entry.TryRetain())
                return entry;

            lock (_writer)
            {
                // Retired entries leave the table under this lock, so whatever is found here is alive.
                if (_active.TryGetValue(key, out entry) && entry.TryRetain())
                    return entry;

                IDynamicTexture texture = create();
                if (texture == null)
                    return null;
                TKey stored = storedKey == null ? key : storedKey(key);
                entry = new SharedTextureEntry(texture, frame, stored);
                var next = new Dictionary<TKey, SharedTextureEntry>(_active);
                next[stored] = entry;
                _active = next;
                created = true;
                return entry;
            }
        }

        internal void Clear()
        {
            lock (_writer)
            {
                foreach (SharedTextureEntry entry in _active.Values)
                    DisposeQuietly(entry.Texture);
                _active = new Dictionary<TKey, SharedTextureEntry>();
            }
        }

        void Sweep(long now)
        {
            lock (_writer)
            {
                if (now < _nextSweepTicks)
                    return;
                Interlocked.Exchange(ref _nextSweepTicks, now + SweepIntervalTicks);
                List<TKey> retired = null;
                foreach (var pair in _active)
                {
                    if (!pair.Value.TryRetire(now, RetentionTicks))
                        continue;
                    if (retired == null)
                        retired = new List<TKey>();
                    retired.Add(pair.Key);
                }
                if (retired == null)
                    return;
                var next = new Dictionary<TKey, SharedTextureEntry>(_active);
                foreach (TKey key in retired)
                {
                    DisposeQuietly(next[key].Texture);
                    next.Remove(key);
                }
                _active = next;
            }
        }

        // The texture may belong to a mod that already unloaded.
        internal static void DisposeQuietly(IDynamicTexture texture)
        {
            try
            {
                texture.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
