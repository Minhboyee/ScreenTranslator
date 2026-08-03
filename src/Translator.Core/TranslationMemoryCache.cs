namespace Translator.Core;

public interface ITranslationMemory
{
    bool TryGet(TranslationMemoryKey key, out string translatedText);

    void Set(TranslationMemoryKey key, string translatedText);
}

public sealed class TranslationMemoryCache : ITranslationMemory
{
    private readonly object gate = new();
    private readonly int capacity;
    private readonly Dictionary<TranslationMemoryKey, CacheEntry> entries = new();
    private readonly LinkedList<TranslationMemoryKey> leastRecentlyUsed = new();

    public TranslationMemoryCache(int capacity = 256)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    public bool TryGet(TranslationMemoryKey key, out string translatedText)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                translatedText = string.Empty;
                return false;
            }

            leastRecentlyUsed.Remove(entry.Node);
            entry.Node = leastRecentlyUsed.AddLast(key);
            translatedText = entry.TranslatedText;
            return true;
        }
    }

    public void Set(TranslationMemoryKey key, string translatedText)
    {
        ArgumentNullException.ThrowIfNull(translatedText);

        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                existing.TranslatedText = translatedText;
                leastRecentlyUsed.Remove(existing.Node);
                existing.Node = leastRecentlyUsed.AddLast(key);
                return;
            }

            var node = leastRecentlyUsed.AddLast(key);
            entries.Add(key, new CacheEntry(translatedText, node));

            if (entries.Count <= capacity)
            {
                return;
            }

            var oldest = leastRecentlyUsed.First!;
            leastRecentlyUsed.RemoveFirst();
            entries.Remove(oldest.Value);
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string translatedText, LinkedListNode<TranslationMemoryKey> node)
        {
            TranslatedText = translatedText;
            Node = node;
        }

        public string TranslatedText { get; set; }

        public LinkedListNode<TranslationMemoryKey> Node { get; set; }
    }
}
