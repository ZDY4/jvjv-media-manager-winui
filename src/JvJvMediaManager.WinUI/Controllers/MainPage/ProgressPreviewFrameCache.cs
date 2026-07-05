using Microsoft.UI.Xaml.Media;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class ProgressPreviewFrameCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, ImageSource> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();

    public ProgressPreviewFrameCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _items.Count;

    public void Clear()
    {
        _items.Clear();
        _nodes.Clear();
        _lru.Clear();
    }

    public bool TryGet(string key, out ImageSource source)
    {
        if (!_items.TryGetValue(key, out source!))
        {
            return false;
        }

        Touch(key);
        return true;
    }

    public void Add(string key, ImageSource source)
    {
        _items[key] = source;
        Touch(key);

        while (_items.Count > _capacity && _lru.First is { } oldest)
        {
            var oldestKey = oldest.Value;
            _lru.RemoveFirst();
            _nodes.Remove(oldestKey);
            _items.Remove(oldestKey);
        }
    }

    private void Touch(string key)
    {
        if (_nodes.Remove(key, out var existingNode))
        {
            _lru.Remove(existingNode);
        }

        _nodes[key] = _lru.AddLast(key);
    }
}
