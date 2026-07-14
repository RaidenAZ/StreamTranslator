namespace StreamTranslator.Core.Subtitles;

public sealed class UtteranceGroupTracker
{
    private const int MaximumSegments = 3;
    private const long MaximumSpanMs = 12000;
    private readonly string _groupPrefix;

    private string? _currentGroupId;
    private long _currentGroupStartMs;
    private long _lastSequence;
    private int _segmentCount;

    public UtteranceGroupTracker(string groupPrefix = "utt")
    {
        if (string.IsNullOrWhiteSpace(groupPrefix))
        {
            throw new ArgumentException("Group prefix is required.", nameof(groupPrefix));
        }

        _groupPrefix = groupPrefix;
    }

    public UtteranceGroupAssignment Assign(
        long sequence,
        long startMs,
        long endMs,
        bool mergeWithPrevious)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (startMs < 0 || endMs <= startMs)
        {
            throw new ArgumentOutOfRangeException(nameof(endMs));
        }

        var canContinue = mergeWithPrevious &&
                          _currentGroupId is not null &&
                          sequence == _lastSequence + 1 &&
                          _segmentCount < MaximumSegments &&
                          endMs - _currentGroupStartMs <= MaximumSpanMs;
        if (!canContinue)
        {
            _currentGroupId = $"{_groupPrefix}-{sequence:000000}";
            _currentGroupStartMs = startMs;
            _segmentCount = 1;
        }
        else
        {
            _segmentCount++;
        }

        _lastSequence = sequence;
        var groupId = _currentGroupId ?? throw new InvalidOperationException("Utterance group was not initialized.");
        return new UtteranceGroupAssignment(
            groupId,
            _segmentCount,
            canContinue,
            _currentGroupStartMs,
            endMs);
    }

    public void CloseCurrentGroup()
    {
        _currentGroupId = null;
        _segmentCount = 0;
    }
}

public sealed record UtteranceGroupAssignment(
    string UtteranceGroupId,
    int SegmentCount,
    bool IsContinuation,
    long GroupStartMs,
    long GroupEndMs);
