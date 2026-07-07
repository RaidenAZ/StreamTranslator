namespace StreamTranslator.Core.Subtitles;

public sealed class SubtitleReorderBuffer
{
    private readonly SortedDictionary<long, SubtitleItem> _pending = [];
    private long _nextSequence;

    public SubtitleReorderBuffer(long firstSequence)
    {
        _nextSequence = firstSequence;
    }

    public IReadOnlyList<SubtitleItem> Add(SubtitleItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Sequence < _nextSequence)
        {
            return [];
        }

        _pending[item.Sequence] = item;

        var released = new List<SubtitleItem>();
        while (_pending.Remove(_nextSequence, out var ready))
        {
            released.Add(ready);
            _nextSequence++;
        }

        return released;
    }
}

