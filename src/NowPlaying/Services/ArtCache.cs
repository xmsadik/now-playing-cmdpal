using System.Collections.Generic;

namespace NowPlaying.Services;

/// <summary>
/// Small LRU of <see cref="ArtImage"/> by content hash (two sessions may share one image). Entries are only
/// forgotten; the PNG files they point to are cleaned up at startup.
/// </summary>
internal sealed class ArtCache(int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ArtImage> _items = [];
    private readonly LinkedList<string> _order = new();

    public ArtImage? TryGet(string key)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var image))
            {
                return null;
            }

            _order.Remove(key);
            _order.AddFirst(key);
            return image;
        }
    }

    /// <summary>Adds the image, or returns the one that is already cached under the same key.</summary>
    public ArtImage GetOrAdd(ArtImage image)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(image.Key, out var existing))
            {
                return existing;
            }

            _items[image.Key] = image;
            _order.AddFirst(image.Key);
            return image;
        }
    }

    /// <summary>Drops least-recently-used entries beyond capacity, except images that are still referenced.</summary>
    public void Trim(IReadOnlySet<ArtImage> inUse)
    {
        lock (_gate)
        {
            var node = _order.Last;
            while (_items.Count > capacity && node is not null)
            {
                var previous = node.Previous;
                if (!inUse.Contains(_items[node.Value]))
                {
                    _items.Remove(node.Value);
                    _order.Remove(node);
                }

                node = previous;
            }
        }
    }
}
