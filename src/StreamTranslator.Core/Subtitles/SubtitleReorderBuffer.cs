namespace StreamTranslator.Core.Subtitles;

public sealed class SubtitleReorderBuffer
{
    private static readonly TimeSpan DefaultMaxGapWait = TimeSpan.FromSeconds(30);
    private const int DefaultMaxPending = 64;

    private readonly SortedDictionary<long, SubtitleItem> _pending = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maxGapWait;
    private readonly int _maxPending;
    private long _nextSequence;
    private DateTimeOffset? _gapSince;

    public SubtitleReorderBuffer(
        long firstSequence,
        TimeProvider? timeProvider = null,
        TimeSpan? maxGapWait = null,
        int? maxPending = null)
    {
        _nextSequence = firstSequence;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxGapWait = maxGapWait ?? DefaultMaxGapWait;
        _maxPending = maxPending ?? DefaultMaxPending;
    }

    /// <summary>Sequences skipped because their terminal item never arrived.</summary>
    public long SkippedSequences { get; private set; }

    public IReadOnlyList<SubtitleItem> Add(SubtitleItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Sequence < _nextSequence)
        {
            return [];
        }

        _pending[item.Sequence] = item;

        var released = ReleaseReady();

        // Every sequence is contractually given a terminal item, but a bug or a
        // cancelled task must not dam the subtitle stream forever. When a gap
        // persists too long (or pending piles up), skip to the oldest waiting
        // item. Recovery is passive: it triggers on the next Add, which live
        // capture produces continuously.
        if (_pending.Count == 0)
        {
            _gapSince = null;
            return released;
        }

        var now = _timeProvider.GetUtcNow();
        _gapSince ??= now;
        if (now - _gapSince >= _maxGapWait || _pending.Count > _maxPending)
        {
            var oldestWaiting = _pending.Keys.First();
            SkippedSequences += oldestWaiting - _nextSequence;
            _nextSequence = oldestWaiting;
            released.AddRange(ReleaseReady());
            _gapSince = _pending.Count == 0 ? null : now;
        }

        return released;
    }

    private List<SubtitleItem> ReleaseReady()
    {
        var released = new List<SubtitleItem>();
        while (_pending.Remove(_nextSequence, out var ready))
        {
            released.Add(ready);
            _nextSequence++;
        }

        return released;
    }
}
