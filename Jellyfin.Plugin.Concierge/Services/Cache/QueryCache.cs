using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Concierge.Services.Cache
{
    /// <summary>
    /// Remembers recent answers so a repeated search costs nothing.
    /// </summary>
    /// <remarks>
    /// The same person retyping the same thing is the commonest search there is, and
    /// this is where that stops costing money. A hit must be free and instant.
    /// <para>
    /// In memory rather than on disk, which is a deviation from the plan's sketch.
    /// The trade: a server restart empties it, and in exchange there is no file to
    /// corrupt, no serialization of a type that changes every release, and no disk
    /// write inside a latency budget measured in milliseconds. A cold cache after a
    /// restart costs a few cents at most.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The cached value.</typeparam>
    public sealed class QueryCache<T>
        where T : class
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);

        /// <summary>Most recently used at the front.</summary>
        private readonly LinkedList<Entry> _order = new();

        private int _capacity;

        public QueryCache(int capacity = 200)
        {
            _capacity = Math.Max(1, capacity);
        }

        /// <summary>Gets how many answers are held.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _byKey.Count;
                }
            }
        }

        /// <summary>Gets how many lookups have hit.</summary>
        public long Hits { get; private set; }

        /// <summary>Gets how many lookups have missed.</summary>
        public long Misses { get; private set; }

        /// <summary>
        /// Resizes the cache, evicting from the cold end if it shrank.
        /// </summary>
        /// <param name="capacity">The new capacity.</param>
        public void Resize(int capacity)
        {
            lock (_gate)
            {
                _capacity = Math.Max(1, capacity);
                Trim();
            }
        }

        /// <summary>
        /// Looks an answer up, promoting it if found.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="value">The cached answer.</param>
        /// <returns>Whether there was one.</returns>
        public bool TryGet(string key, out T? value)
        {
            lock (_gate)
            {
                if (_byKey.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    Hits++;
                    value = node.Value.Value;
                    return true;
                }

                Misses++;
                value = null;
                return false;
            }
        }

        /// <summary>
        /// Stores an answer.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="value">The answer.</param>
        public void Set(string key, T value)
        {
            ArgumentNullException.ThrowIfNull(value);

            lock (_gate)
            {
                if (_byKey.TryGetValue(key, out var existing))
                {
                    _order.Remove(existing);
                    _byKey.Remove(key);
                }

                var node = _order.AddFirst(new Entry(key, value));
                _byKey[key] = node;
                Trim();
            }
        }

        /// <summary>
        /// Empties the cache.
        /// </summary>
        /// <remarks>
        /// Called when the index is rebuilt or deleted. The generation is already part
        /// of every key, so this is belt and braces rather than the mechanism — but it
        /// also frees the memory, which the key alone would not.
        /// </remarks>
        public void Clear()
        {
            lock (_gate)
            {
                _byKey.Clear();
                _order.Clear();
            }
        }

        private void Trim()
        {
            while (_byKey.Count > _capacity && _order.Last is { } last)
            {
                _byKey.Remove(last.Value.Key);
                _order.RemoveLast();
            }
        }

        private sealed record Entry(string Key, T Value);
    }
}
