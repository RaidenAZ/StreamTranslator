using StreamTranslator.Core.Configuration;

namespace StreamTranslator.Audio.Segmentation;

public sealed class AdaptiveEndpointController
{
    private readonly int _startSpeechMs;
    private readonly List<PauseSample> _pauseSamples = [];
    private readonly Queue<long> _adjustmentTimes = new();
    private PendingCut? _pendingCut;
    private long? _silenceStartedAtMs;
    private long? _pendingSpeechStartedAtMs;
    private int _pendingSpeechMs;
    private bool _speechConfirmed;
    private int _consecutiveQuickResumes;
    private long? _lastAdjustmentAtMs;
    private long? _lastConfirmedSpeechAtMs;

    public AdaptiveEndpointController(VadEndpointMode mode, int fixedEndSilenceMs, int startSpeechMs = 96)
    {
        if (startSpeechMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSpeechMs));
        }

        _startSpeechMs = startSpeechMs;
        Mode = mode;
        var profile = VadEndpointProfiles.Get(mode, fixedEndSilenceMs);
        EffectiveEndSilenceMs = profile.InitialEndSilenceMs;
        MinimumEndSilenceMs = profile.MinimumEndSilenceMs;
        MaximumEndSilenceMs = profile.MaximumEndSilenceMs;
        InitialEndSilenceMs = EffectiveEndSilenceMs;
    }

    public VadEndpointMode Mode { get; }
    public int EffectiveEndSilenceMs { get; private set; }
    public int InitialEndSilenceMs { get; }
    public int MinimumEndSilenceMs { get; }
    public int MaximumEndSilenceMs { get; }
    public bool IsAdaptive => MinimumEndSilenceMs != MaximumEndSilenceMs;
    public int PauseSampleCount => _pauseSamples.Count;

    public event EventHandler<EndpointAdjustment>? EndpointAdjusted;

    public AdaptiveEndpointObservation ObserveVad(long startMs, int durationMs, bool isSpeech)
    {
        if (startMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startMs));
        }

        if (durationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        if (!isSpeech)
        {
            _pendingSpeechStartedAtMs = null;
            _pendingSpeechMs = 0;
            if (_speechConfirmed)
            {
                _speechConfirmed = false;
                _silenceStartedAtMs = startMs;
            }

            var idleAdjustment = IsAdaptive ? TryHandleIdle(startMs + durationMs) : null;
            return new AdaptiveEndpointObservation(EffectiveEndSilenceMs, null, idleAdjustment);
        }

        if (_speechConfirmed)
        {
            _lastConfirmedSpeechAtMs = startMs + durationMs;
            return new AdaptiveEndpointObservation(EffectiveEndSilenceMs, null, null);
        }

        _pendingSpeechStartedAtMs ??= startMs;
        _pendingSpeechMs += durationMs;
        if (_pendingSpeechMs < _startSpeechMs)
        {
            return new AdaptiveEndpointObservation(EffectiveEndSilenceMs, null, null);
        }

        var confirmedSpeechStartMs = _pendingSpeechStartedAtMs.Value;
        _pendingSpeechStartedAtMs = null;
        _pendingSpeechMs = 0;
        _speechConfirmed = true;
        _lastConfirmedSpeechAtMs = startMs + durationMs;

        QuickResumeSignal? quickResume = null;
        EndpointAdjustment? adjustment = null;
        if (_pendingCut is not null)
        {
            var postCutGapMs = Math.Max(0, confirmedSpeechStartMs - _pendingCut.CutAtMs);
            var completePauseMs = checked(_pendingCut.EndSilenceMs + (int)postCutGapMs);
            var isQuickResume = completePauseMs <= 800;
            if (IsAdaptive)
            {
                AddPauseSample(completePauseMs, startMs + durationMs, isQuickResume);
            }

            if (isQuickResume)
            {
                quickResume = new QuickResumeSignal(completePauseMs, ShouldMergeWithPreviousSegment: IsAdaptive);
                if (IsAdaptive)
                {
                    _consecutiveQuickResumes++;
                    if (_pauseSamples.Count >= 3 && _consecutiveQuickResumes >= 2)
                    {
                        var p75PauseMs = CalculateP75PauseMs();
                        adjustment = TryAdjust(
                            startMs + durationMs,
                            Math.Min(MaximumEndSilenceMs, EffectiveEndSilenceMs + 50),
                            EndpointAdjustmentReason.QuickResume,
                            p75PauseMs,
                            Math.Clamp(p75PauseMs + 50, MinimumEndSilenceMs, MaximumEndSilenceMs));
                    }
                }
            }
            else
            {
                _consecutiveQuickResumes = 0;
            }

            _pendingCut = null;
        }
        else if (IsAdaptive && _silenceStartedAtMs is not null)
        {
            var pauseMs = checked((int)Math.Max(0, confirmedSpeechStartMs - _silenceStartedAtMs.Value));
            AddPauseSample(pauseMs, startMs + durationMs, isQuickResume: false);
            _consecutiveQuickResumes = 0;
            adjustment = TryLowerEndpoint(startMs + durationMs);
        }

        _silenceStartedAtMs = null;
        return new AdaptiveEndpointObservation(EffectiveEndSilenceMs, quickResume, adjustment);
    }

    public void NotifySegmentCut(long cutAtMs, SpeechSegmentCutReason cutReason)
    {
        if (cutReason != SpeechSegmentCutReason.Silence)
        {
            return;
        }

        _pendingCut = new PendingCut(cutAtMs, EffectiveEndSilenceMs);
        _speechConfirmed = false;
        _pendingSpeechStartedAtMs = null;
        _pendingSpeechMs = 0;
    }

    private void AddPauseSample(int durationMs, long observedAtMs, bool isQuickResume)
    {
        _pauseSamples.Add(new PauseSample(durationMs, observedAtMs, isQuickResume));
        _pauseSamples.RemoveAll(sample => observedAtMs - sample.ObservedAtMs > 15000);
        while (_pauseSamples.Count > 8)
        {
            _pauseSamples.RemoveAt(0);
        }
    }

    private EndpointAdjustment? TryLowerEndpoint(long timestampMs)
    {
        if (_pauseSamples.Count < 6 || _pauseSamples.Any(static sample => sample.IsQuickResume))
        {
            return null;
        }

        var p75PauseMs = CalculateP75PauseMs();
        var targetEndSilenceMs = Math.Clamp(p75PauseMs + 50, MinimumEndSilenceMs, MaximumEndSilenceMs);
        if (targetEndSilenceMs >= EffectiveEndSilenceMs)
        {
            return null;
        }

        return TryAdjust(
            timestampMs,
            Math.Max(targetEndSilenceMs, EffectiveEndSilenceMs - 25),
            EndpointAdjustmentReason.StablePauses,
            p75PauseMs,
            targetEndSilenceMs);
    }

    private EndpointAdjustment? TryHandleIdle(long timestampMs)
    {
        if (_lastConfirmedSpeechAtMs is null || timestampMs - _lastConfirmedSpeechAtMs.Value < 10000)
        {
            return null;
        }

        _pauseSamples.Clear();
        _consecutiveQuickResumes = 0;
        _pendingCut = null;
        if (EffectiveEndSilenceMs == InitialEndSilenceMs)
        {
            return null;
        }

        var step = EffectiveEndSilenceMs > InitialEndSilenceMs ? -25 : 25;
        var requested = step < 0
            ? Math.Max(InitialEndSilenceMs, EffectiveEndSilenceMs + step)
            : Math.Min(InitialEndSilenceMs, EffectiveEndSilenceMs + step);
        return TryAdjust(
            timestampMs,
            requested,
            EndpointAdjustmentReason.IdleReturn,
            InitialEndSilenceMs,
            InitialEndSilenceMs);
    }

    private int CalculateP75PauseMs()
    {
        if (_pauseSamples.Count == 0)
        {
            return EffectiveEndSilenceMs;
        }

        var ordered = _pauseSamples.Select(static sample => sample.DurationMs).Order().ToArray();
        var index = Math.Max(0, (int)Math.Ceiling(ordered.Length * 0.75) - 1);
        return ordered[index];
    }

    private EndpointAdjustment? TryAdjust(
        long timestampMs,
        int requestedEndSilenceMs,
        EndpointAdjustmentReason reason,
        int p75PauseMs,
        int targetEndSilenceMs)
    {
        if (requestedEndSilenceMs == EffectiveEndSilenceMs || !CanAdjust(timestampMs))
        {
            return null;
        }

        var previous = EffectiveEndSilenceMs;
        EffectiveEndSilenceMs = Math.Clamp(requestedEndSilenceMs, MinimumEndSilenceMs, MaximumEndSilenceMs);
        _lastAdjustmentAtMs = timestampMs;
        _adjustmentTimes.Enqueue(timestampMs);

        var adjustment = new EndpointAdjustment(
            timestampMs,
            previous,
            EffectiveEndSilenceMs,
            reason,
            _pauseSamples.Count,
            p75PauseMs,
            targetEndSilenceMs);
        EndpointAdjusted?.Invoke(this, adjustment);
        return adjustment;
    }

    private bool CanAdjust(long timestampMs)
    {
        while (_adjustmentTimes.Count > 0 && timestampMs - _adjustmentTimes.Peek() > 10000)
        {
            _adjustmentTimes.Dequeue();
        }

        return (_lastAdjustmentAtMs is null || timestampMs - _lastAdjustmentAtMs.Value >= 2000) &&
               _adjustmentTimes.Count < 2;
    }

    private sealed record PendingCut(long CutAtMs, int EndSilenceMs);
    private sealed record PauseSample(int DurationMs, long ObservedAtMs, bool IsQuickResume);
}

public sealed record AdaptiveEndpointObservation(
    int EffectiveEndSilenceMs,
    QuickResumeSignal? QuickResume,
    EndpointAdjustment? Adjustment);

public sealed record QuickResumeSignal(int CompletePauseMs, bool ShouldMergeWithPreviousSegment);

public sealed record EndpointAdjustment(
    long TimestampMs,
    int PreviousEndSilenceMs,
    int CurrentEndSilenceMs,
    EndpointAdjustmentReason Reason,
    int SampleCount,
    int P75PauseMs,
    int TargetEndSilenceMs);

public enum EndpointAdjustmentReason
{
    QuickResume,
    StablePauses,
    IdleReturn
}
