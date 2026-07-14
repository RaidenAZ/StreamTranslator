using StreamTranslator.Audio.Vad;

namespace StreamTranslator.Audio.Segmentation;

public sealed class SpeechSegmenter
{
    private readonly SpeechSegmenterOptions _options;
    private readonly List<PcmAudioFrame> _frames = [];
    private readonly List<PcmAudioFrame> _pendingStartFrames = [];
    private readonly List<PcmAudioFrame> _preRollFrames = [];
    private int _pendingSpeechMs;
    private int _trailingSilenceMs;
    private int _softBreakSilenceMs;
    private long? _hardLimitStartMs;

    public SpeechSegmenter(SpeechSegmenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StartSpeechMs <= 0 || options.PreRollMs < 0 || options.EndSilenceMs <= 0 ||
            options.SoftBreakSilenceMs <= 0 || options.MinSegmentMs < 0 || options.SoftMaxSegmentMs <= 0 ||
            options.HardMaxSegmentMs <= options.SoftMaxSegmentMs || options.OverlapMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Speech segmenter options are inconsistent.");
        }

        _options = options;
    }

    public CompletedSpeechSegment? Push(PcmAudioFrame frame, VadDecision decision)
    {
        return Push(frame, decision, _options.EndSilenceMs);
    }

    public CompletedSpeechSegment? Push(PcmAudioFrame frame, VadDecision decision, int effectiveEndSilenceMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (effectiveEndSilenceMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveEndSilenceMs));
        }

        if (_frames.Count == 0)
        {
            if (!decision.IsSpeech)
            {
                foreach (var pendingFrame in _pendingStartFrames)
                {
                    AddPreRoll(pendingFrame);
                }

                AddPreRoll(frame);
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

            var speechStartMs = _pendingStartFrames[0].StartMs;
            _frames.AddRange(_preRollFrames);
            _frames.AddRange(_pendingStartFrames);
            _preRollFrames.Clear();
            _pendingStartFrames.Clear();
            _pendingSpeechMs = 0;
            _hardLimitStartMs = speechStartMs;
            _trailingSilenceMs = 0;
            _softBreakSilenceMs = 0;
            return null;
        }

        _frames.Add(frame);
        _trailingSilenceMs = decision.IsSpeech ? 0 : _trailingSilenceMs + frame.DurationMs;
        _softBreakSilenceMs = decision.IsSpeech ? 0 : _softBreakSilenceMs + frame.DurationMs;

        if (_trailingSilenceMs >= effectiveEndSilenceMs && CurrentDurationMs >= _options.MinSegmentMs)
        {
            return Complete(SpeechSegmentCutReason.Silence, frame.EndMs, 0);
        }

        if (_softBreakSilenceMs >= _options.SoftBreakSilenceMs &&
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

    private void AddPreRoll(PcmAudioFrame frame)
    {
        if (_options.PreRollMs == 0)
        {
            _preRollFrames.Clear();
            return;
        }

        _preRollFrames.Add(frame);
        while (_preRollFrames.Count > 0 &&
               _preRollFrames[^1].EndMs - _preRollFrames[0].StartMs > _options.PreRollMs)
        {
            _preRollFrames.RemoveAt(0);
        }
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
        _softBreakSilenceMs = 0;
        _hardLimitStartMs = _frames.Count == 0 ? null : endMs;

        return completed;
    }
}
