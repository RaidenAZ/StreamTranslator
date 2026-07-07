using StreamTranslator.Audio.Vad;

namespace StreamTranslator.Audio.Segmentation;

public sealed class SpeechSegmenter
{
    private readonly SpeechSegmenterOptions _options;
    private readonly List<PcmAudioFrame> _frames = [];
    private readonly List<PcmAudioFrame> _pendingStartFrames = [];
    private int _pendingSpeechMs;
    private int _trailingSilenceMs;
    private long? _hardLimitStartMs;

    public SpeechSegmenter(SpeechSegmenterOptions options)
    {
        _options = options;
    }

    public CompletedSpeechSegment? Push(PcmAudioFrame frame, VadDecision decision)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_frames.Count == 0)
        {
            if (!decision.IsSpeech)
            {
                _pendingStartFrames.Clear();
                _pendingSpeechMs = 0;
                return null;
            }

            _pendingStartFrames.Add(frame);
            _pendingSpeechMs += frame.DurationMs;
            if (_pendingSpeechMs < _options.StartSpeechMs)
            {
                return null;
            }

            _frames.AddRange(_pendingStartFrames);
            _pendingStartFrames.Clear();
            _pendingSpeechMs = 0;
            _hardLimitStartMs = _frames[0].StartMs;
            _trailingSilenceMs = 0;
            return null;
        }

        _frames.Add(frame);
        _trailingSilenceMs = decision.IsSpeech ? 0 : _trailingSilenceMs + frame.DurationMs;

        if (_trailingSilenceMs >= _options.EndSilenceMs && CurrentDurationMs >= _options.MinSegmentMs)
        {
            return Complete(SpeechSegmentCutReason.Silence, frame.EndMs, 0);
        }

        if (!decision.IsSpeech &&
            HardLimitElapsedMs(frame.EndMs) > _options.SoftMaxSegmentMs &&
            CurrentDurationMs >= _options.MinSegmentMs)
        {
            return Complete(SpeechSegmentCutReason.SoftMax, frame.EndMs, 0);
        }

        if (HardLimitElapsedMs(frame.EndMs) >= _options.HardMaxSegmentMs)
        {
            return Complete(SpeechSegmentCutReason.HardMax, frame.EndMs, _options.OverlapMs);
        }

        return null;
    }

    private int CurrentDurationMs => _frames.Count == 0 ? 0 : (int)(_frames[^1].EndMs - _frames[0].StartMs);

    private int HardLimitElapsedMs(long endMs)
    {
        return _hardLimitStartMs is null ? 0 : (int)(endMs - _hardLimitStartMs.Value);
    }

    private CompletedSpeechSegment Complete(SpeechSegmentCutReason cutReason, long endMs, int overlapMs)
    {
        var samples = _frames.SelectMany(static frame => frame.Samples).ToArray();
        var completed = new CompletedSpeechSegment(
            _frames[0].StartMs,
            endMs,
            _frames[0].SampleRate,
            samples,
            cutReason,
            overlapMs);

        var retainedFrames = overlapMs > 0
            ? _frames.Where(frame => frame.EndMs > endMs - overlapMs).ToArray()
            : [];

        _frames.Clear();
        _frames.AddRange(retainedFrames);
        _pendingStartFrames.Clear();
        _pendingSpeechMs = 0;
        _trailingSilenceMs = 0;
        _hardLimitStartMs = _frames.Count == 0 ? null : endMs;

        return completed;
    }
}
