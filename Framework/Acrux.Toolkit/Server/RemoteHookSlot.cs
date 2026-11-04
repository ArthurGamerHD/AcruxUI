using System;

namespace Acrux.Toolkit.Server
{
    /// <summary>
    /// The client handlers of one mirrored virtual member. Every client mod compiles its own copy of the wrapper
    /// types, and its handler casts the peer to those types, so a call is routed to the handler of the client that
    /// owns the peer (the client whose ownership test accepts it), never to whichever client connected last.
    /// </summary>
    public sealed class RemoteHookSlot<T> where T : class
    {
        struct Entry
        {
            public GeneratedTexturePool Owner;
            public T Handler;
        }

        // Copy on write: hooks run on every draw and layout pass, registration only when a client connects.
        Entry[] _entries = new Entry[0];

        // A client that connected without an ownership test (an older client runtime).
        T _unowned;

        internal void Set(GeneratedTexturePool owner, T handler)
        {
            if (owner == null)
            {
                _unowned = handler;
                return;
            }
            var entries = _entries;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Owner != owner)
                    continue;
                var replaced = (Entry[])entries.Clone();
                replaced[i].Handler = handler;
                _entries = replaced;
                return;
            }
            var added = new Entry[entries.Length + 1];
            Array.Copy(entries, added, entries.Length);
            added[entries.Length] = new Entry { Owner = owner, Handler = handler };
            _entries = added;
        }

        internal void Remove(GeneratedTexturePool owner)
        {
            var entries = _entries;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Owner != owner)
                    continue;
                var removed = new Entry[entries.Length - 1];
                Array.Copy(entries, removed, i);
                Array.Copy(entries, i + 1, removed, i, entries.Length - i - 1);
                _entries = removed;
                return;
            }
        }

        /// <summary>The handler of the client that owns <paramref name="peer"/>, or null to run the toolkit's own code.</summary>
        public T For(object peer)
        {
            if (peer == null)
                return null;
            var entries = _entries;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Owner.Owns(peer))
                    return entries[i].Handler;
            }
            return _unowned;
        }
    }
}
