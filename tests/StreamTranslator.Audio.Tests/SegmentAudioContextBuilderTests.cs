using StreamTranslator.Audio.Encoding;
using StreamTranslator.Audio.Segmentation;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class SegmentAudioContextBuilderTests
{
    [TestMethod]
    public void AudioClipPreparer_TrimsExcessTrailingSilenceAndAppliesEdgeFade()
    {
        const int sampleRate = 16000;
        // 1000ms total audio with 500ms trailing silence -> should trim 340ms, leaving 660ms (10560 samples).
        var samples = Enumerable.Repeat<short>(8000, sampleRate).ToArray();

        var prepared = AudioClipPreparer.PrepareForAsr(
            samples,
            sampleRate,
            trailingSilenceMs: 500,
            maxTrailingSilenceMs: 160,
            fadeMs: 10);

        Assert.AreEqual(sampleRate * 660 / 1000, prepared.Length);
        Assert.AreEqual(0, prepared[0]);
        Assert.AreEqual(0, prepared[^1]);
        Assert.AreEqual(8000, prepared[prepared.Length / 2]);
        // Original array must remain untouched.
        Assert.AreEqual(8000, samples[0]);
    }

    [TestMethod]
    public void Prepare_FullUtteranceReRecognition_ConcatenatesGroupWithoutDuplicatingOverlap()
    {
        var builder = new SegmentAudioContextBuilder(
            maxGroupSegments: 3,
            maxGroupSpanMs: 12_000,
            maxConcatenatedGapMs: 300,
            targetLeftContextMs: 2400);

        // Segment 1: 0..2000ms with 400ms trailing silence (trimmed to 120ms inter-chunk tail -> emitted through 1720ms)
        var seg1 = MakeSegment(0, 2000, SpeechSegmentCutReason.Silence, trailingSilenceMs: 400, amplitude: 1200);
        var first = builder.Prepare(1, "utt-1", isGroupContinuation: false, seg1, enableAudioContext: true);

        Assert.AreEqual(AsrAudioContextMode.Standalone, first.ContextMode);
        Assert.IsFalse(first.ReplacesGroupSourceText);

        // Segment 2: 2320..4000ms (600ms gap from 1720ms -> clamped to 300ms silence gap)
        var seg2 = MakeSegment(2320, 4000, SpeechSegmentCutReason.Silence, trailingSilenceMs: 200, amplitude: 2400);
        var second = builder.Prepare(2, "utt-1", isGroupContinuation: true, seg2, enableAudioContext: true);

        Assert.AreEqual(AsrAudioContextMode.FullUtteranceReRecognition, second.ContextMode);
        Assert.IsTrue(second.ReplacesGroupSourceText);
        Assert.AreEqual(2, second.CombinedSegmentCount);
        Assert.AreEqual(0, second.AudioStartMs);
        Assert.AreEqual(4000, second.AudioEndMs);

        // Expected length: 1720ms (seg1 kept) + 300ms (clamped gap) + 1640ms (seg2 1680ms - 40ms excess silence) = 3660ms
        var expectedSamples = 16000 * 3660 / 1000;
        Assert.AreEqual(expectedSamples, second.Samples.Length);
        // Verify silence gap exists in the middle.
        Assert.AreEqual(0, second.Samples[16000 * 1850 / 1000]);
    }

    [TestMethod]
    public void Prepare_LeftAudioContext_PrependsContextAfterHardMaxWithoutDuplicateOverlap()
    {
        var builder = new SegmentAudioContextBuilder(
            targetLeftContextMs: 2400,
            maxTrailingSilenceMs: 160,
            fadeMs: 0);

        // Segment 1: 0..5000ms HardMax with 600ms overlap.
        var seg1 = MakeSegment(0, 5000, SpeechSegmentCutReason.HardMax, overlapMs: 600, amplitude: 1500);
        _ = builder.Prepare(1, "utt-1", isGroupContinuation: false, seg1, enableAudioContext: true);

        // Segment 2: 4400..9000ms (already includes 600ms overlap 4400..5000ms).
        // Builder should add 1800ms extra prefix (2600..4400ms) -> total left context 2400ms, total audio 6400ms.
        var seg2 = MakeSegment(4400, 9000, SpeechSegmentCutReason.Silence, trailingSilenceMs: 160, amplitude: 2500);
        var prepared = builder.Prepare(2, "utt-2", isGroupContinuation: false, seg2, enableAudioContext: true);

        Assert.AreEqual(AsrAudioContextMode.LeftAudioContext, prepared.ContextMode);
        Assert.IsFalse(prepared.ReplacesGroupSourceText);
        Assert.AreEqual(2400, prepared.LeftContextMs);
        Assert.AreEqual(2600, prepared.AudioStartMs);
        Assert.AreEqual(16000 * 6400 / 1000, prepared.Samples.Length);
        // Verify prefix came from seg1 (1500) and new segment kept seg2 (2500).
        Assert.AreEqual(1500, prepared.Samples[100]);
        Assert.AreEqual(2500, prepared.Samples[^100]);
    }

    [TestMethod]
    public void Prepare_DisabledContext_AlwaysProducesStandaloneAudio()
    {
        var builder = new SegmentAudioContextBuilder();

        var seg1 = MakeSegment(0, 5000, SpeechSegmentCutReason.HardMax, overlapMs: 600, amplitude: 1000);
        var seg2 = MakeSegment(4400, 8000, SpeechSegmentCutReason.Silence, trailingSilenceMs: 100, amplitude: 2000);

        var first = builder.Prepare(1, "utt-1", isGroupContinuation: false, seg1, enableAudioContext: false);
        var second = builder.Prepare(2, "utt-1", isGroupContinuation: true, seg2, enableAudioContext: false);

        Assert.AreEqual(AsrAudioContextMode.Standalone, first.ContextMode);
        Assert.AreEqual(AsrAudioContextMode.Standalone, second.ContextMode);
        Assert.IsFalse(second.ReplacesGroupSourceText);
    }

    [TestMethod]
    public void InvalidateGroup_PreventsConcatenatingFailedOrNoSpeechSegment()
    {
        var builder = new SegmentAudioContextBuilder();
        var seg1 = MakeSegment(0, 1500, SpeechSegmentCutReason.Silence, trailingSilenceMs: 300, amplitude: 500);
        var seg2 = MakeSegment(1800, 3200, SpeechSegmentCutReason.Silence, trailingSilenceMs: 160, amplitude: 1800);

        _ = builder.Prepare(1, "utt-1", isGroupContinuation: false, seg1, enableAudioContext: true);
        builder.InvalidateGroup("utt-1");

        var second = builder.Prepare(2, "utt-1", isGroupContinuation: true, seg2, enableAudioContext: true);

        Assert.AreEqual(AsrAudioContextMode.Standalone, second.ContextMode);
        Assert.IsFalse(second.ReplacesGroupSourceText);
    }

    private static CompletedSpeechSegment MakeSegment(
        long startMs,
        long endMs,
        SpeechSegmentCutReason cutReason,
        int overlapMs = 0,
        int trailingSilenceMs = 0,
        short amplitude = 1000)
    {
        const int sampleRate = 16000;
        var count = checked((int)((endMs - startMs) * sampleRate / 1000));
        var samples = Enumerable.Repeat(amplitude, count).ToArray();
        return new CompletedSpeechSegment(startMs, endMs, sampleRate, samples, cutReason, overlapMs, trailingSilenceMs);
    }
}
