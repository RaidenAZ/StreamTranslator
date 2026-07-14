using StreamTranslator.Audio.Segmentation;
using StreamTranslator.Core.Configuration;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class AdaptiveEndpointControllerTests
{
    [TestMethod]
    public void Constructor_UsesConfirmedModeProfiles()
    {
        var lowLatency = new AdaptiveEndpointController(VadEndpointMode.LowLatency, fixedEndSilenceMs: 350);
        var balanced = new AdaptiveEndpointController(VadEndpointMode.Balanced, fixedEndSilenceMs: 350);
        var sentenceComplete = new AdaptiveEndpointController(VadEndpointMode.SentenceComplete, fixedEndSilenceMs: 350);
        var fixedValue = new AdaptiveEndpointController(VadEndpointMode.Fixed, fixedEndSilenceMs: 350);

        AssertProfile(lowLatency, 250, 200, 400, isAdaptive: true);
        AssertProfile(balanced, 400, 280, 600, isAdaptive: true);
        AssertProfile(sentenceComplete, 600, 400, 800, isAdaptive: true);
        AssertProfile(fixedValue, 350, 350, 350, isAdaptive: false);
    }

    [TestMethod]
    public void ObserveVad_ThreeSamplesIncludingTwoQuickResumesRaiseEndpointByFiftyMilliseconds()
    {
        var controller = new AdaptiveEndpointController(VadEndpointMode.Balanced, fixedEndSilenceMs: 400);

        controller.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);
        StablePause(controller, silenceStartMs: 100, pauseMs: 200);
        var firstResume = QuickResume(controller, silenceStartMs: 400, resumeStartMs: 900);
        var secondResume = QuickResume(controller, silenceStartMs: 1000, resumeStartMs: 1500);

        Assert.IsNotNull(firstResume.QuickResume);
        Assert.AreEqual(500, firstResume.QuickResume.CompletePauseMs);
        Assert.IsNull(firstResume.Adjustment);
        Assert.IsNotNull(secondResume.QuickResume);
        Assert.IsTrue(secondResume.QuickResume.ShouldMergeWithPreviousSegment);
        Assert.IsNotNull(secondResume.Adjustment);
        Assert.AreEqual(EndpointAdjustmentReason.QuickResume, secondResume.Adjustment.Reason);
        Assert.AreEqual(400, secondResume.Adjustment.PreviousEndSilenceMs);
        Assert.AreEqual(450, secondResume.Adjustment.CurrentEndSilenceMs);
        Assert.AreEqual(450, controller.EffectiveEndSilenceMs);
    }

    [TestMethod]
    public void ObserveVad_SixStablePausesLowerEndpointByTwentyFiveMilliseconds()
    {
        var controller = new AdaptiveEndpointController(VadEndpointMode.Balanced, fixedEndSilenceMs: 400);
        controller.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);

        AdaptiveEndpointObservation observation = default!;
        var silenceStartMs = 100L;
        for (var index = 0; index < 6; index++)
        {
            controller.ObserveVad(silenceStartMs, durationMs: 200, isSpeech: false);
            observation = controller.ObserveVad(silenceStartMs + 200, durationMs: 100, isSpeech: true);
            silenceStartMs += 300;
        }

        Assert.IsNotNull(observation.Adjustment);
        Assert.AreEqual(EndpointAdjustmentReason.StablePauses, observation.Adjustment.Reason);
        Assert.AreEqual(6, observation.Adjustment.SampleCount);
        Assert.AreEqual(200, observation.Adjustment.P75PauseMs);
        Assert.AreEqual(280, observation.Adjustment.TargetEndSilenceMs);
        Assert.AreEqual(375, controller.EffectiveEndSilenceMs);
    }

    [TestMethod]
    public void ObserveVad_TenSecondsWithoutConfirmedSpeechClearsSamplesAndReturnsGradually()
    {
        var controller = new AdaptiveEndpointController(VadEndpointMode.Balanced, fixedEndSilenceMs: 400);
        controller.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);
        StablePause(controller, silenceStartMs: 100, pauseMs: 200);
        QuickResume(controller, silenceStartMs: 400, resumeStartMs: 900);
        QuickResume(controller, silenceStartMs: 1000, resumeStartMs: 1500);

        var idleObservation = controller.ObserveVad(startMs: 11600, durationMs: 100, isSpeech: false);

        Assert.IsNotNull(idleObservation.Adjustment);
        Assert.AreEqual(EndpointAdjustmentReason.IdleReturn, idleObservation.Adjustment.Reason);
        Assert.AreEqual(0, idleObservation.Adjustment.SampleCount);
        Assert.AreEqual(425, controller.EffectiveEndSilenceMs);
        Assert.AreEqual(0, controller.PauseSampleCount);
    }

    [TestMethod]
    public void ObserveVad_AdjustsAtMostTwiceWithinTenSeconds()
    {
        var controller = new AdaptiveEndpointController(VadEndpointMode.Balanced, fixedEndSilenceMs: 400);
        var adjustments = new List<EndpointAdjustment>();
        controller.EndpointAdjusted += (_, adjustment) => adjustments.Add(adjustment);
        controller.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);

        StablePause(controller, silenceStartMs: 100, pauseMs: 200);
        QuickResume(controller, silenceStartMs: 400, resumeStartMs: 900);
        QuickResume(controller, silenceStartMs: 1000, resumeStartMs: 1500);
        controller.ObserveVad(startMs: 1600, durationMs: 1500, isSpeech: true);
        QuickResume(controller, silenceStartMs: 3100, resumeStartMs: 3650);
        controller.ObserveVad(startMs: 3750, durationMs: 1350, isSpeech: true);
        var fourthResume = QuickResume(controller, silenceStartMs: 5100, resumeStartMs: 5700);

        Assert.AreEqual(2, adjustments.Count);
        Assert.IsNull(fourthResume.Adjustment);
        Assert.AreEqual(500, controller.EffectiveEndSilenceMs);
    }

    [TestMethod]
    public void ObserveVad_KeepsEightRecentPausesAndExpiresOldSamples()
    {
        var controller = new AdaptiveEndpointController(VadEndpointMode.SentenceComplete, fixedEndSilenceMs: 600);
        controller.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);

        var silenceStartMs = 100L;
        for (var index = 0; index < 9; index++)
        {
            controller.ObserveVad(silenceStartMs, durationMs: 500, isSpeech: false);
            controller.ObserveVad(silenceStartMs + 500, durationMs: 100, isSpeech: true);
            silenceStartMs += 600;
        }

        Assert.AreEqual(8, controller.PauseSampleCount);

        controller.ObserveVad(startMs: 5500, durationMs: 16000, isSpeech: true);
        controller.ObserveVad(startMs: 21500, durationMs: 500, isSpeech: false);
        controller.ObserveVad(startMs: 22000, durationMs: 100, isSpeech: true);

        Assert.AreEqual(1, controller.PauseSampleCount);
    }

    [TestMethod]
    public void ObserveVad_QuickResumeBoundaryIsEightHundredMilliseconds()
    {
        var accepted = new AdaptiveEndpointController(VadEndpointMode.Balanced, fixedEndSilenceMs: 400);
        accepted.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);
        var acceptedResume = QuickResume(accepted, silenceStartMs: 100, resumeStartMs: 900);

        var rejected = new AdaptiveEndpointController(VadEndpointMode.Balanced, fixedEndSilenceMs: 400);
        rejected.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);
        var rejectedResume = QuickResume(rejected, silenceStartMs: 100, resumeStartMs: 901);

        Assert.IsNotNull(acceptedResume.QuickResume);
        Assert.AreEqual(800, acceptedResume.QuickResume.CompletePauseMs);
        Assert.IsNull(rejectedResume.QuickResume);
    }

    [TestMethod]
    public void ObserveVad_FixedModeMeasuresQuickResumeWithoutLearningOrRequestingMerge()
    {
        var controller = new AdaptiveEndpointController(VadEndpointMode.Fixed, fixedEndSilenceMs: 350);

        controller.ObserveVad(startMs: 0, durationMs: 100, isSpeech: true);
        controller.ObserveVad(startMs: 100, durationMs: 350, isSpeech: false);
        controller.NotifySegmentCut(cutAtMs: 450, SpeechSegmentCutReason.Silence);
        var resume = controller.ObserveVad(startMs: 500, durationMs: 100, isSpeech: true);

        Assert.IsNotNull(resume.QuickResume);
        Assert.IsFalse(resume.QuickResume.ShouldMergeWithPreviousSegment);
        Assert.IsNull(resume.Adjustment);
        Assert.AreEqual(0, controller.PauseSampleCount);
        Assert.AreEqual(350, controller.EffectiveEndSilenceMs);
    }

    private static AdaptiveEndpointObservation QuickResume(
        AdaptiveEndpointController controller,
        long silenceStartMs,
        long resumeStartMs)
    {
        var endpointAtCut = controller.EffectiveEndSilenceMs;
        var cutAtMs = silenceStartMs + endpointAtCut;
        controller.ObserveVad(silenceStartMs, endpointAtCut, isSpeech: false);
        controller.NotifySegmentCut(cutAtMs, SpeechSegmentCutReason.Silence);
        return controller.ObserveVad(resumeStartMs, durationMs: 100, isSpeech: true);
    }

    private static AdaptiveEndpointObservation StablePause(
        AdaptiveEndpointController controller,
        long silenceStartMs,
        int pauseMs)
    {
        controller.ObserveVad(silenceStartMs, pauseMs, isSpeech: false);
        return controller.ObserveVad(silenceStartMs + pauseMs, durationMs: 100, isSpeech: true);
    }

    private static void AssertProfile(
        AdaptiveEndpointController controller,
        int effective,
        int minimum,
        int maximum,
        bool isAdaptive)
    {
        Assert.AreEqual(effective, controller.EffectiveEndSilenceMs);
        Assert.AreEqual(minimum, controller.MinimumEndSilenceMs);
        Assert.AreEqual(maximum, controller.MaximumEndSilenceMs);
        Assert.AreEqual(isAdaptive, controller.IsAdaptive);
    }
}
