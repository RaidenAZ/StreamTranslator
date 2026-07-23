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
    private bool _idleInitialReported;
    private int _consecutiveQuickResumes;
    private long? _lastAdjustmentAtMs;
    private long? _lastConfirmedSpeechAtMs;
    private long? _lastIdleEvaluationAtMs;

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

            var idleResult = IsAdaptive
                ? TryHandleIdle(startMs + durationMs)
                : (Adjustment: null, Evaluation: null);
            return new AdaptiveEndpointObservation(
                EffectiveEndSilenceMs,
                null,
                idleResult.Adjustment,
                idleResult.Evaluation);
        }

        _idleInitialReported = false;
        _lastIdleEvaluationAtMs = null;
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
        EndpointEvaluation? evaluation = null;
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
                    var p75PauseMs = CalculateP75PauseMs();
                    var targetEndSilenceMs = Math.Clamp(
                        p75PauseMs + 50,
                        MinimumEndSilenceMs,
                        MaximumEndSilenceMs);
                    var decision = EndpointEvaluationDecision.WaitingForSamples;
                    if (_pauseSamples.Count >= 3)
                    {
                        if (_consecutiveQuickResumes < 2)
                        {
                            decision = EndpointEvaluationDecision.WaitingForQuickResumes;
                        }
                        else
                        {
                            var requestedEndSilenceMs = Math.Min(MaximumEndSilenceMs, EffectiveEndSilenceMs + 50);
                            adjustment = TryAdjust(
                                startMs + durationMs,
                                requestedEndSilenceMs,
                                EndpointAdjustmentReason.QuickResume,
                                p75PauseMs,
                                targetEndSilenceMs);
                            decision = adjustment is not null
                                ? EndpointEvaluationDecision.Adjusted
                                : ClassifyBlockedAdjustment(startMs + durationMs, requestedEndSilenceMs);
                        }
                    }

                    evaluation = CreateEvaluation(
                        startMs + durationMs,
                        EndpointEvaluationSignal.QuickResume,
                        decision,
                        p75PauseMs,
                        targetEndSilenceMs);
                }
                else
                {
                    evaluation = CreateEvaluation(
                        startMs + durationMs,
                        EndpointEvaluationSignal.QuickResume,
                        EndpointEvaluationDecision.FixedMode,
                        CalculateP75PauseMs(),
                        EffectiveEndSilenceMs);
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
            evaluation = EvaluateStablePause(startMs + durationMs, out adjustment);
        }

        _silenceStartedAtMs = null;
        return new AdaptiveEndpointObservation(EffectiveEndSilenceMs, quickResume, adjustment, evaluation);
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

    private EndpointEvaluation EvaluateStablePause(long timestampMs, out EndpointAdjustment? adjustment)
    {
        var p75PauseMs = CalculateP75PauseMs();
        var targetEndSilenceMs = Math.Clamp(p75PauseMs + 50, MinimumEndSilenceMs, MaximumEndSilenceMs);
        if (_pauseSamples.Count < 6 || _pauseSamples.Any(static sample => sample.IsQuickResume))
        {
            adjustment = null;
            return CreateEvaluation(
                timestampMs,
                EndpointEvaluationSignal.StablePause,
                EndpointEvaluationDecision.WaitingForStablePauses,
                p75PauseMs,
                targetEndSilenceMs);
        }

        if (targetEndSilenceMs >= EffectiveEndSilenceMs)
        {
            adjustment = null;
            return CreateEvaluation(
                timestampMs,
                EndpointEvaluationSignal.StablePause,
                EndpointEvaluationDecision.TargetUnchanged,
                p75PauseMs,
                targetEndSilenceMs);
        }

        adjustment = TryAdjust(
            timestampMs,
            Math.Max(targetEndSilenceMs, EffectiveEndSilenceMs - 25),
            EndpointAdjustmentReason.StablePauses,
            p75PauseMs,
            targetEndSilenceMs);
        return CreateEvaluation(
            timestampMs,
            EndpointEvaluationSignal.StablePause,
            adjustment is not null
                ? EndpointEvaluationDecision.Adjusted
                : ClassifyBlockedAdjustment(timestampMs, Math.Max(targetEndSilenceMs, EffectiveEndSilenceMs - 25)),
            p75PauseMs,
            targetEndSilenceMs);
    }

    private (EndpointAdjustment? Adjustment, EndpointEvaluation? Evaluation) TryHandleIdle(long timestampMs)
    {
        if (_lastConfirmedSpeechAtMs is null || timestampMs - _lastConfirmedSpeechAtMs.Value < 10000)
        {
            return (null, null);
        }

        _pauseSamples.Clear();
        _consecutiveQuickResumes = 0;
        _pendingCut = null;
        if (EffectiveEndSilenceMs == InitialEndSilenceMs)
        {
            if (_idleInitialReported)
            {
                return (null, null);
            }

            _idleInitialReported = true;
            _lastIdleEvaluationAtMs = timestampMs;
            return (
                null,
                CreateEvaluation(
                    timestampMs,
                    EndpointEvaluationSignal.Idle,
                    EndpointEvaluationDecision.IdleNoChange,
                    InitialEndSilenceMs,
                    InitialEndSilenceMs));
        }

        if (_lastIdleEvaluationAtMs is not null && timestampMs - _lastIdleEvaluationAtMs.Value < 2000)
        {
            return (null, null);
        }

        _lastIdleEvaluationAtMs = timestampMs;

        var step = EffectiveEndSilenceMs > InitialEndSilenceMs ? -25 : 25;
        var requested = step < 0
            ? Math.Max(InitialEndSilenceMs, EffectiveEndSilenceMs + step)
            : Math.Min(InitialEndSilenceMs, EffectiveEndSilenceMs + step);
        var adjustment = TryAdjust(
            timestampMs,
            requested,
            EndpointAdjustmentReason.IdleReturn,
            InitialEndSilenceMs,
            InitialEndSilenceMs);
        if (adjustment is null)
        {
            return (
                null,
                CreateEvaluation(
                    timestampMs,
                    EndpointEvaluationSignal.Idle,
                    ClassifyBlockedAdjustment(timestampMs, requested),
                    InitialEndSilenceMs,
                    InitialEndSilenceMs));
        }

        _idleInitialReported = EffectiveEndSilenceMs == InitialEndSilenceMs;
        return (
            adjustment,
            CreateEvaluation(
                timestampMs,
                EndpointEvaluationSignal.Idle,
                EndpointEvaluationDecision.IdleReturning,
                InitialEndSilenceMs,
                InitialEndSilenceMs));
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

    private EndpointEvaluation CreateEvaluation(
        long timestampMs,
        EndpointEvaluationSignal signal,
        EndpointEvaluationDecision decision,
        int p75PauseMs,
        int targetEndSilenceMs)
    {
        return new EndpointEvaluation(
            timestampMs,
            signal,
            decision,
            EffectiveEndSilenceMs,
            _pauseSamples.Count,
            p75PauseMs,
            targetEndSilenceMs,
            _consecutiveQuickResumes,
            RecentAdjustmentCount(timestampMs),
            CooldownRemainingMs(timestampMs));
    }

    private EndpointEvaluationDecision ClassifyBlockedAdjustment(long timestampMs, int requestedEndSilenceMs)
    {
        if (requestedEndSilenceMs == EffectiveEndSilenceMs)
        {
            return EndpointEvaluationDecision.AtBoundary;
        }

        if (CooldownRemainingMs(timestampMs) > 0)
        {
            return EndpointEvaluationDecision.Cooldown;
        }

        if (RecentAdjustmentCount(timestampMs) >= 2)
        {
            return EndpointEvaluationDecision.RateLimited;
        }

        return EndpointEvaluationDecision.TargetUnchanged;
    }

    private int RecentAdjustmentCount(long timestampMs)
    {
        while (_adjustmentTimes.Count > 0 && timestampMs - _adjustmentTimes.Peek() > 10000)
        {
            _adjustmentTimes.Dequeue();
        }

        return _adjustmentTimes.Count;
    }

    private int CooldownRemainingMs(long timestampMs)
    {
        if (_lastAdjustmentAtMs is null)
        {
            return 0;
        }

        return Math.Max(0, 2000 - checked((int)(timestampMs - _lastAdjustmentAtMs.Value)));
    }

    private bool CanAdjust(long timestampMs)
    {
        RecentAdjustmentCount(timestampMs);
        return (_lastAdjustmentAtMs is null || timestampMs - _lastAdjustmentAtMs.Value >= 2000) &&
               _adjustmentTimes.Count < 2;
    }

    private sealed record PendingCut(long CutAtMs, int EndSilenceMs);
    private sealed record PauseSample(int DurationMs, long ObservedAtMs, bool IsQuickResume);
}

public sealed record AdaptiveEndpointObservation(
    int EffectiveEndSilenceMs,
    QuickResumeSignal? QuickResume,
    EndpointAdjustment? Adjustment,
    EndpointEvaluation? Evaluation = null);

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

public sealed record EndpointEvaluation(
    long TimestampMs,
    EndpointEvaluationSignal Signal,
    EndpointEvaluationDecision Decision,
    int EffectiveEndSilenceMs,
    int SampleCount,
    int P75PauseMs,
    int TargetEndSilenceMs,
    int ConsecutiveQuickResumes,
    int RecentAdjustmentCount,
    int CooldownRemainingMs);

public enum EndpointEvaluationSignal
{
    QuickResume,
    StablePause,
    Idle
}

public enum EndpointEvaluationDecision
{
    Adjusted,
    WaitingForSamples,
    WaitingForQuickResumes,
    WaitingForStablePauses,
    TargetUnchanged,
    Cooldown,
    RateLimited,
    AtBoundary,
    IdleReturning,
    IdleNoChange,
    FixedMode
}
