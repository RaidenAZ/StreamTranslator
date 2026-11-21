using StreamTranslator.Audio.Encoding;

namespace StreamTranslator.Audio.Segmentation;

public enum AsrAudioContextMode
{
    Standalone,
    FullUtteranceReRecognition,
    LeftAudioContext
}

public sealed record PreparedAsrAudio(
    short[] Samples,
    int SampleRate,
    long AudioStartMs,
    long AudioEndMs,
    AsrAudioContextMode ContextMode,
    int LeftContextMs,
    int CombinedSegmentCount,
    bool ReplacesGroupSourceText);

/// <summary>
/// Builds context-enriched PCM clips for stateless ASR APIs (MiMo-V2.5-ASR, DeepSeek-ASR):
///   - Layer B1 (FullUtteranceReRecognition): concatenates adjacent segments belonging to
///     the same utterance group (QuickResume or incomplete clause continuation) so ASR
///     re-transcribes the complete utterance with full acoustic context.
///   - Layer B2 (LeftAudioContext): prepends ~2.4s of acoustic left context from a prior
///     HardMax/SoftMax segment, snapped to a quiet energy valley, so continuous speech cuts
///     still enter ASR with strong preceding context.
/// </summary>
public sealed class SegmentAudioContextBuilder
{
    private const int InterChunkMaxTrailingSilenceMs = 120;
    private const int MinLeftContextToActivateMs = 400;
    private const int MaxContiguousGapForLeftContextMs = 350;

    private readonly int _maxGroupSegments;
    private readonly long _maxGroupSpanMs;
    private readonly int _maxConcatenatedGapMs;
    private readonly int _targetLeftContextMs;
    private readonly int _maxTrailingSilenceMs;
    private readonly int _fadeMs;

    private readonly List<StoredSegment> _groupSegments = [];
    private string? _activeGroupId;
    private StoredSegment? _lastSegment;

    public SegmentAudioContextBuilder(
        int maxGroupSegments = 3,
        long maxGroupSpanMs = 12_000,
        int maxConcatenatedGapMs = 320,
        int targetLeftContextMs = 2_400,
        int maxTrailingSilenceMs = AudioClipPreparer.DefaultMaxTrailingSilenceMs,
        int fadeMs = AudioClipPreparer.DefaultFadeMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGroupSegments, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGroupSpanMs, 1000);
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcatenatedGapMs);
        ArgumentOutOfRangeException.ThrowIfNegative(targetLeftContextMs);

        _maxGroupSegments = maxGroupSegments;
        _maxGroupSpanMs = maxGroupSpanMs;
        _maxConcatenatedGapMs = maxConcatenatedGapMs;
        _targetLeftContextMs = targetLeftContextMs;
        _maxTrailingSilenceMs = maxTrailingSilenceMs;
        _fadeMs = fadeMs;
    }

    public PreparedAsrAudio Prepare(
        long sequence,
        string utteranceGroupId,
        bool isGroupContinuation,
        CompletedSpeechSegment segment,
        bool enableAudioContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(utteranceGroupId);
        ArgumentNullException.ThrowIfNull(segment);

        var stored = new StoredSegment(
            sequence,
            segment.StartMs,
            segment.EndMs,
            segment.SampleRate,
            segment.Samples,
            segment.CutReason,
            segment.OverlapMs,
            segment.TrailingSilenceMs);

        if (!enableAudioContext)
        {
            _activeGroupId = null;
            _groupSegments.Clear();
            _lastSegment = stored;
            var plain = AudioClipPreparer.PrepareForAsr(
                segment.Samples,
                segment.SampleRate,
                segment.TrailingSilenceMs,
                _maxTrailingSilenceMs,
                _fadeMs);
            return new PreparedAsrAudio(
                plain,
                segment.SampleRate,
                segment.StartMs,
                segment.EndMs,
                AsrAudioContextMode.Standalone,
                LeftContextMs: 0,
                CombinedSegmentCount: 1,
                ReplacesGroupSourceText: false);
        }

        // Layer B1: Full-utterance audio concatenation re-recognition for merged segments.
        if (isGroupContinuation && CanContinueActiveGroup(utteranceGroupId, stored))
        {
            _groupSegments.Add(stored);
            _lastSegment = stored;
            var combined = BuildConcatenatedGroupAudio(_groupSegments);
            return new PreparedAsrAudio(
                combined,
                segment.SampleRate,
                _groupSegments[0].StartMs,
                segment.EndMs,
                AsrAudioContextMode.FullUtteranceReRecognition,
                LeftContextMs: 0,
                CombinedSegmentCount: _groupSegments.Count,
                ReplacesGroupSourceText: true);
        }

        var previous = _lastSegment;
        _activeGroupId = utteranceGroupId;
        _groupSegments.Clear();
        _groupSegments.Add(stored);
        _lastSegment = stored;

        // Layer B2: Audio left-context sliding window after continuous HardMax / SoftMax cuts.
        if (previous is not null &&
            _targetLeftContextMs > 0 &&
            previous.SampleRate == stored.SampleRate &&
            previous.CutReason is SpeechSegmentCutReason.HardMax or SpeechSegmentCutReason.SoftMax &&
            stored.StartMs <= previous.EndMs + MaxContiguousGapForLeftContextMs)
        {
            var (withContext, contextStartMs, totalLeftContextMs) = BuildLeftContextAudio(previous, stored);
            if (totalLeftContextMs >= MinLeftContextToActivateMs)
            {
                return new PreparedAsrAudio(
                    withContext,
                    segment.SampleRate,
                    contextStartMs,
                    segment.EndMs,
                    AsrAudioContextMode.LeftAudioContext,
                    LeftContextMs: totalLeftContextMs,
                    CombinedSegmentCount: 1,
                    ReplacesGroupSourceText: false);
            }
        }

        var standalone = AudioClipPreparer.PrepareForAsr(
            segment.Samples,
            segment.SampleRate,
            segment.TrailingSilenceMs,
            _maxTrailingSilenceMs,
            _fadeMs);
        return new PreparedAsrAudio(
            standalone,
            segment.SampleRate,
            segment.StartMs,
            segment.EndMs,
            AsrAudioContextMode.Standalone,
            LeftContextMs: 0,
            CombinedSegmentCount: 1,
            ReplacesGroupSourceText: false);
    }

    public void InvalidateGroup(string? utteranceGroupId)
    {
        if (string.IsNullOrWhiteSpace(utteranceGroupId) ||
            string.Equals(_activeGroupId, utteranceGroupId, StringComparison.Ordinal))
        {
            _activeGroupId = null;
            _groupSegments.Clear();
        }
    }

    public void Reset()
    {
        _activeGroupId = null;
        _groupSegments.Clear();
        _lastSegment = null;
    }

    private bool CanContinueActiveGroup(string utteranceGroupId, StoredSegment next)
    {
        if (!string.Equals(_activeGroupId, utteranceGroupId, StringComparison.Ordinal) ||
            _groupSegments.Count == 0 ||
            _groupSegments.Count >= _maxGroupSegments)
        {
            return false;
        }

        var first = _groupSegments[0];
        return first.SampleRate == next.SampleRate &&
               next.EndMs - first.StartMs <= _maxGroupSpanMs;
    }

    private short[] BuildConcatenatedGroupAudio(IReadOnlyList<StoredSegment> chunks)
    {
        var sampleRate = chunks[0].SampleRate;
        var slices = new List<AudioSlice>(chunks.Count * 2);
        var totalSamples = 0;
        var previousEmittedEndMs = chunks[0].StartMs;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var isLast = i == chunks.Count - 1;
            var startOffsetSamples = 0;

            if (i > 0)
            {
                if (chunk.StartMs < previousEmittedEndMs)
                {
                    var overlapMs = previousEmittedEndMs - chunk.StartMs;
                    startOffsetSamples = Math.Clamp(
                        MsToSamples(overlapMs, sampleRate),
                        0,
                        chunk.Samples.Length);
                }
                else if (chunk.StartMs > previousEmittedEndMs)
                {
                    var gapMs = Math.Clamp(chunk.StartMs - previousEmittedEndMs, 0, _maxConcatenatedGapMs);
                    var gapSamples = MsToSamples(gapMs, sampleRate);
                    if (gapSamples > 0)
                    {
                        slices.Add(new AudioSlice(null, 0, gapSamples));
                        totalSamples += gapSamples;
                    }
                }
            }

            var keepSamples = AudioClipPreparer.ComputeTrimmedSampleCount(
                chunk.Samples.Length,
                sampleRate,
                chunk.TrailingSilenceMs,
                isLast ? _maxTrailingSilenceMs : InterChunkMaxTrailingSilenceMs);

            var length = Math.Max(0, keepSamples - startOffsetSamples);
            if (length > 0)
            {
                slices.Add(new AudioSlice(chunk.Samples, startOffsetSamples, length));
                totalSamples += length;
            }

            var trimmedTailMs = (chunk.Samples.Length - keepSamples) * 1000L / sampleRate;
            previousEmittedEndMs = chunk.EndMs - trimmedTailMs;
        }

        var output = new short[totalSamples];
        var cursor = 0;
        foreach (var slice in slices)
        {
            if (slice.Source is not null)
            {
                Array.Copy(slice.Source, slice.Offset, output, cursor, slice.Length);
            }

            cursor += slice.Length;
        }

        AudioClipPreparer.ApplyEdgeFadeInPlace(output, sampleRate, _fadeMs);
        return output;
    }

    private (short[] Samples, long ContextStartMs, int TotalLeftContextMs) BuildLeftContextAudio(
        StoredSegment previous,
        StoredSegment current)
    {
        var sampleRate = current.SampleRate;
        var currentTrimmed = AudioClipPreparer.TrimTrailingSilence(
            current.Samples,
            sampleRate,
            current.TrailingSilenceMs,
            _maxTrailingSilenceMs);

        var existingOverlapMs = Math.Max(0, (int)(previous.EndMs - current.StartMs));
        var desiredExtraMs = Math.Max(0, _targetLeftContextMs - existingOverlapMs);

        int boundarySample;
        long previousBoundaryEndMs;
        if (current.StartMs < previous.EndMs)
        {
            var offsetMs = Math.Clamp(current.StartMs - previous.StartMs, 0, previous.EndMs - previous.StartMs);
            boundarySample = Math.Clamp(MsToSamples(offsetMs, sampleRate), 0, previous.Samples.Length);
            previousBoundaryEndMs = current.StartMs;
        }
        else
        {
            boundarySample = AudioClipPreparer.ComputeTrimmedSampleCount(
                previous.Samples.Length,
                sampleRate,
                previous.TrailingSilenceMs,
                InterChunkMaxTrailingSilenceMs);
            var trimmedTailMs = (previous.Samples.Length - boundarySample) * 1000L / sampleRate;
            previousBoundaryEndMs = previous.EndMs - trimmedTailMs;
        }

        var prefixLength = 0;
        var snappedStartSample = boundarySample;
        if (desiredExtraMs > 0 && boundarySample > 0)
        {
            var rawStartSample = Math.Max(0, boundarySample - MsToSamples(desiredExtraMs, sampleRate));
            snappedStartSample = AudioClipPreparer.FindLowEnergySplitOffset(
                previous.Samples.AsSpan(0, boundarySample),
                sampleRate,
                rawStartSample,
                searchRadiusMs: 250);
            prefixLength = Math.Max(0, boundarySample - snappedStartSample);
        }

        var gapSamples = 0;
        if (current.StartMs > previousBoundaryEndMs && prefixLength > 0)
        {
            var gapMs = Math.Clamp(current.StartMs - previousBoundaryEndMs, 0, 160);
            gapSamples = MsToSamples(gapMs, sampleRate);
        }

        var output = new short[prefixLength + gapSamples + currentTrimmed.Length];
        if (prefixLength > 0)
        {
            Array.Copy(previous.Samples, snappedStartSample, output, 0, prefixLength);
        }

        if (currentTrimmed.Length > 0)
        {
            Array.Copy(currentTrimmed, 0, output, prefixLength + gapSamples, currentTrimmed.Length);
        }

        AudioClipPreparer.ApplyEdgeFadeInPlace(output, sampleRate, _fadeMs);

        var addedPrefixMs = (int)((long)prefixLength * 1000L / sampleRate);
        var totalLeftContextMs = existingOverlapMs + addedPrefixMs;
        var contextStartMs = prefixLength > 0
            ? previous.StartMs + (long)snappedStartSample * 1000L / sampleRate
            : current.StartMs;

        return (output, contextStartMs, totalLeftContextMs);
    }

    private static int MsToSamples(long durationMs, int sampleRate)
    {
        if (durationMs <= 0 || sampleRate <= 0)
        {
            return 0;
        }

        return checked((int)Math.Clamp(durationMs * sampleRate / 1000L, 0, int.MaxValue));
    }

    private sealed record StoredSegment(
        long Sequence,
        long StartMs,
        long EndMs,
        int SampleRate,
        short[] Samples,
        SpeechSegmentCutReason CutReason,
        int OverlapMs,
        int TrailingSilenceMs);

    private readonly record struct AudioSlice(short[]? Source, int Offset, int Length);
}
