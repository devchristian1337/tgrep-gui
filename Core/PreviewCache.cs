namespace TgrepGui.Core;

// Single-threaded: owned by the UI dispatcher. The selected preview stays outside the cache.
public sealed class PreviewCache<T>(int capacity, long byteBudget, Action<T> evict) where T : class
{
    private readonly LinkedList<(T Item, long Bytes)> entries = new();
    public long RetainedBytes { get; private set; }
    public int Count => entries.Count;

    public void Take(T item)
    {
        for (var node = entries.First; node != null; node = node.Next)
        {
            if (!ReferenceEquals(node.Value.Item, item)) continue;
            RetainedBytes -= node.Value.Bytes;
            entries.Remove(node);
            return;
        }
    }

    public void Keep(T item, long bytes)
    {
        Take(item);
        entries.AddLast((item, bytes));
        RetainedBytes += bytes;
        while (entries.Count > capacity || RetainedBytes > byteBudget)
        {
            var oldest = entries.First!.Value;
            entries.RemoveFirst();
            RetainedBytes -= oldest.Bytes;
            evict(oldest.Item);
        }
    }

    public void Clear()
    {
        foreach (var entry in entries) evict(entry.Item);
        entries.Clear();
        RetainedBytes = 0;
    }
}
