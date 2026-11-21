using StreamTranslator.Audio.Vad;

namespace StreamTranslator.Audio.Segmentation;

public sealed class SpeechSegmenter
{
    private const float RmsNormalizationCeiling = 0.18f;
    private readonly SpeechSegmenterOptions _options;
    private readonly List<BufferedFrame> _frames = [];
    private readonly List<BufferedFrame> _pendingStartFrames = [];
    private readonly List<BufferedFrame> _preRollFrames = [];
    private int _pendingSpeechMs;
    private int _trailingSilenceMs;
    private int _softBreakSilenceMs;
    private int _pendingSilenceInterruptMs;
    private long? _hardLimitStartMs;

    public SpeechSegmenter(SpeechSegmenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StartSpeechMs <= 0 || options.PreRollMs < 0 || options.EndSilenceMs <= 0 ||
            options.SoftBreakSilenceMs <= 0 || options.MinSegmentMs < 0 || options.SoftMaxSegmentMs <= 0 ||
            options.HardMaxSegmentMs <= options.SoftMaxSegmentMs || options.OverlapMs < 0 ||
            options.SilenceGlitchToleranceMs < 0 || options.ValleyLookbackMs < 0)
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

        var buffered = new BufferedFrame(frame, decision.IsSpeech, decision.Probability, CalculateNormalizedRms(frame.Samples));

        if (_frames.Count == 0)
        {
            if (!decision.IsSpeech)
            {
                foreach (var pendingFrame in _pendingStartFrames)
                {
                    AddPreRoll(pendingFrame);
                }

                AddPreRoll(buffered);
                _pendingStartFrames.Clear();
                _pendingSpeechMs = 0;
                return null;
            }

            _pendingStartFrames.Add(buffered);
            _pendingSpeechMs += frame.DurationMs;
            if (_pendingSpeechMs < _options.StartSpeechMs)
            {
                return null;
            }

            var speechStartMs = _pendingStartFrames[0].Frame.StartMs;
            _frames.AddRange(_preRollFrames);
            _frames.AddRange(_pendingStartFrames);
            _preRollFrames.Clear();
            _pendingStartFrames.Clear();
            _pendingSpeechMs = 0;
            _hardLimitStartMs = speechStartMs;
            _trailingSilenceMs = 0;
            _softBreakSilenceMs = 0;
            _pendingSilenceInterruptMs = 0;
            return null;
        }

        _frames.Add(buffered);

        var effectiveGlitchToleranceMs = Math.Min(
            _options.SilenceGlitchToleranceMs,
            Math.Max(0, _options.StartSpeechMs - 1));

        if (!decision.IsSpeech)
        {
            _pendingSilenceInterruptMs = 0;
            _trailingSilenceMs += frame.DurationMs;
            _softBreakSilenceMs += frame.DurationMs;
        }
        else if ((_trailingSilenceMs > 0 || _softBreakSilenceMs > 0) && effectiveGlitchToleranceMs > 0)
        {
            // Ignore isolated sub-confirmation spikes (e.g. a single 32ms percussion/click frame)
            // during an ongoing pause so natural sentence boundaries aren't repeatedly reset.
            _pendingSilenceInterruptMs += frame.DurationMs;
            if (_pendingSilenceInterruptMs > effectiveGlitchToleranceMs)
            {
                _trailingSilenceMs = 0;
                _softBreakSilenceMs = 0;
                _pendingSilenceInterruptMs = 0;
            }
        }
        else
        {
            _trailingSilenceMs = 0;
            _softBreakSilenceMs = 0;
            _pendingSilenceInterruptMs = 0;
        }

        if (!decision.IsSpeech &&
            _trailingSilenceMs >= effectiveEndSilenceMs &&
            CurrentDurationMs >= _options.MinSegmentMs)
        {
            return Complete(SpeechSegmentCutReason.Silence, _frames.Count - 1, 0, _trailingSilenceMs);
        }

        if (!decision.IsSpeech &&
            _softBreakSilenceMs >= _options.SoftBreakSilenceMs &&
            HardLimitElapsedMs(frame.EndMs) > _options.SoftMaxSegmentMs &&
            CurrentDurationMs >= _options.MinSegmentMs)
        {
            return Complete(SpeechSegmentCutReason.SoftMax, _frames.Count - 1, 0, _softBreakSilenceMs);
        }

        if (HardLimitElapsedMs(frame.EndMs) >= _options.HardMaxSegmentMs)
        {
            var cutIndex = FindHardMaxValleyCutIndex(frame.EndMs);
            return Complete(SpeechSegmentCutReason.HardMax, cutIndex, _options.OverlapMs, _trailingSilenceMs);
        }

        return null;
    }

    private int FindHardMaxValleyCutIndex(long latestEndMs)
    {
        var lastIndex = _frames.Count - 1;
        if (_options.ValleyLookbackMs <= 0 || _frames.Count < 4)
        {
            return lastIndex;
        }

        var segmentStartMs = _frames[0].Frame.StartMs;
        var hardStartMs = _hardLimitStartMs ?? segmentStartMs;
        var minCutEndMs = Math.Max(
            segmentStartMs + _options.MinSegmentMs,
            hardStartMs + (long)_options.HardMaxSegmentMs * 6 / 10);
        var searchWindowStartMs = Math.Max(minCutEndMs, latestEndMs - _options.ValleyLookbackMs);

        var tailScore = WindowAcousticScore(lastIndex);
        var bestIndex = -1;
        var bestScore = float.PositiveInfinity;

        for (var i = 1; i < lastIndex; i++)
        {
            if (_frames[i].Frame.EndMs < searchWindowStartMs)
            {
                continue;
            }

            var score = WindowAcousticScore(i);
            if (score < bestScore - 0.01f || (MathF.Abs(score - bestScore) <= 0.01f && i > bestIndex))
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return lastIndex;
        }

        var clearlyQuieterThanTail = bestScore <= tailScore - 0.12f;
        var genuineLowValley = bestScore < 0.40f && bestScore < tailScore - 0.04f;
        return clearlyQuieterThanTail || genuineLowValley ? bestIndex : lastIndex;
    }

    private float WindowAcousticScore(int index)
    {
        var current = FrameAcousticScore(_frames[index]);
        if (index > 0 && _frames[index].Frame.DurationMs < 64)
        {
            return (current + FrameAcousticScore(_frames[index - 1])) * 0.5f;
        }

        return current;
    }

    private static float FrameAcousticScore(BufferedFrame frame)
    {
        return 0.70f * Math.Clamp(frame.SpeechProbability, 0f, 1f) +
               0.30f * Math.Clamp(frame.NormalizedRms, 0f, 1f);
    }

    private static float CalculateNormalizedRms(short[] samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        var sum = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var normalized = samples[i] / 32768d;
            sum += normalized * normalized;
        }

        var rms = (float)Math.Sqrt(sum / samples.Length);
        return Math.Clamp(rms / RmsNormalizationCeiling, 0f, 1f);
    }

    private void AddPreRoll(BufferedFrame frame)
    {
        if (_options.PreRollMs == 0)
        {
            _preRollFrames.Clear();
            return;
        }

        _preRollFrames.Add(frame);
        while (_preRollFrames.Count > 0 &&
               _preRollFrames[^1].Frame.EndMs - _preRollFrames[0].Frame.StartMs > _options.PreRollMs)
        {
            _preRollFrames.RemoveAt(0);
        }
    }

    private int CurrentDurationMs => _frames.Count == 0 ? 0 : (int)(_frames[^1].Frame.EndMs - _frames[0].Frame.StartMs);

    private int HardLimitElapsedMs(long endMs)
    {
        return _hardLimitStartMs is null ? 0 : (int)(endMs - _hardLimitStartMs.Value);
    }

    private CompletedSpeechSegment Complete(
        SpeechSegmentCutReason cutReason,
        int cutIndex,
        int overlapMs,
        int currentTrailingSilenceMs)
    {
        var emittedCount = cutIndex + 1;
        var cutEndMs = _frames[cutIndex].Frame.EndMs;
        var sampleCount = 0;
        for (var i = 0; i < emittedCount; i++)
        {
            sampleCount += _frames[i].Frame.Samples.Length;
        }

        var samples = new short[sampleCount];
        var offset = 0;
        for (var i = 0; i < emittedCount; i++)
        {
            var src = _frames[i].Frame.Samples;
            Array.Copy(src, 0, samples, offset, src.Length);
            offset += src.Length;
        }

        var emittedTrailingSilenceMs = cutIndex == _frames.Count - 1
            ? currentTrailingSilenceMs
            : CountTrailingSilenceMs(emittedCount);

        var completed = new CompletedSpeechSegment(
            _frames[0].Frame.StartMs,
            cutEndMs,
            _frames[0].Frame.SampleRate,
            samples,
            cutReason,
            overlapMs,
            emittedTrailingSilenceMs);

        var retainedFrames = (overlapMs > 0 || cutIndex < _frames.Count - 1)
            ? _frames.Where(item => item.Frame.EndMs > cutEndMs - overlapMs).ToArray()
            : [];

        _frames.Clear();
        _frames.AddRange(retainedFrames);
        _pendingStartFrames.Clear();
        _pendingSpeechMs = 0;
        _pendingSilenceInterruptMs = 0;

        // Reconstruct trailing silence only from frames captured after the cut point.
        var postCutSilenceMs = 0;
        for (var i = _frames.Count - 1; i >= 0; i--)
        {
            if (_frames[i].Frame.StartMs < cutEndMs || _frames[i].IsSpeech)
            {
                break;
            }

            postCutSilenceMs += _frames[i].Frame.DurationMs;
        }

        _trailingSilenceMs = postCutSilenceMs;
        _softBreakSilenceMs = postCutSilenceMs;
        _hardLimitStartMs = _frames.Count == 0 ? null : cutEndMs;

        return completed;
    }

    private int CountTrailingSilenceMs(int count)
    {
        var total = 0;
        for (var i = count - 1; i >= 0; i--)
        {
            if (_frames[i].IsSpeech)
            {
                break;
            }

            total += _frames[i].Frame.DurationMs;
        }

        return total;
    }

    private readonly record struct BufferedFrame(
        PcmAudioFrame Frame,
        bool IsSpeech,
        float SpeechProbability,
        float NormalizedRms);
}
