using StreamTranslator.Audio.Segmentation;
using StreamTranslator.Audio.Vad;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class SpeechSegmenterTests
{
    [TestMethod]
    public void Push_CompletesSegmentAfterConfiguredEndSilence()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = 300,
            MinSegmentMs = 0,
            SoftMaxSegmentMs = 5000,
            HardMaxSegmentMs = 10000,
            OverlapMs = 0
        });

        CompletedSpeechSegment? completed = null;

        foreach (var frame in Frames(0, 3, speech: true))
        {
            completed = segmenter.Push(frame, new VadDecision(true, 0.9f));
        }

        foreach (var frame in Frames(300, 2, speech: false))
        {
            completed = segmenter.Push(frame, new VadDecision(false, 0.1f));
            Assert.IsNull(completed);
        }

        completed = segmenter.Push(Frame(500, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(SpeechSegmentCutReason.Silence, completed.CutReason);
        Assert.AreEqual(0, completed.StartMs);
        Assert.AreEqual(600, completed.EndMs);
    }

    [TestMethod]
    public void Push_UsesCurrentEffectiveEndSilence()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = 600,
            MinSegmentMs = 0,
            SoftMaxSegmentMs = 5000,
            HardMaxSegmentMs = 10000,
            OverlapMs = 0
        });

        Assert.IsNull(segmenter.Push(Frame(0, speech: true), new VadDecision(true, 0.9f), 400));
        Assert.IsNull(segmenter.Push(Frame(100, speech: false), new VadDecision(false, 0.1f), 400));
        Assert.IsNull(segmenter.Push(Frame(200, speech: false), new VadDecision(false, 0.1f), 400));

        var completed = segmenter.Push(Frame(300, speech: false), new VadDecision(false, 0.1f), 300);

        Assert.IsNotNull(completed);
        Assert.AreEqual(SpeechSegmentCutReason.Silence, completed.CutReason);
        Assert.AreEqual(400, completed.EndMs);
    }

    [TestMethod]
    public void Push_CompletesSegmentAtHardMaxDuringContinuousSpeech()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = 300,
            MinSegmentMs = 0,
            SoftMaxSegmentMs = 400,
            HardMaxSegmentMs = 500,
            OverlapMs = 200
        });

        CompletedSpeechSegment? completed = null;
        foreach (var frame in Frames(0, 4, speech: true))
        {
            completed = segmenter.Push(frame, new VadDecision(true, 0.9f));
            Assert.IsNull(completed);
        }

        completed = segmenter.Push(Frame(400, speech: true), new VadDecision(true, 0.9f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(SpeechSegmentCutReason.HardMax, completed.CutReason);
        Assert.AreEqual(0, completed.StartMs);
        Assert.AreEqual(500, completed.EndMs);
        Assert.AreEqual(200, completed.OverlapMs);
    }

    [TestMethod]
    public void Push_CompletesSegmentAtSoftMaxOnFirstNaturalBreak()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = 300,
            MinSegmentMs = 0,
            SoftBreakSilenceMs = 100,
            SoftMaxSegmentMs = 400,
            HardMaxSegmentMs = 1000,
            OverlapMs = 200
        });

        CompletedSpeechSegment? completed = null;
        foreach (var frame in Frames(0, 4, speech: true))
        {
            completed = segmenter.Push(frame, new VadDecision(true, 0.9f));
            Assert.IsNull(completed);
        }

        completed = segmenter.Push(Frame(400, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(SpeechSegmentCutReason.SoftMax, completed.CutReason);
        Assert.AreEqual(0, completed.StartMs);
        Assert.AreEqual(500, completed.EndMs);
        Assert.AreEqual(0, completed.OverlapMs);
    }

    [TestMethod]
    public void Push_RetainsOverlapFramesAfterHardMax()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = 300,
            MinSegmentMs = 0,
            SoftMaxSegmentMs = 400,
            HardMaxSegmentMs = 500,
            OverlapMs = 200
        });

        foreach (var frame in Frames(0, 5, speech: true))
        {
            segmenter.Push(frame, new VadDecision(true, 0.9f));
        }

        foreach (var frame in Frames(500, 2, speech: true))
        {
            segmenter.Push(frame, new VadDecision(true, 0.9f));
        }

        foreach (var frame in Frames(700, 2, speech: false))
        {
            segmenter.Push(frame, new VadDecision(false, 0.1f));
        }

        var completed = segmenter.Push(Frame(900, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(SpeechSegmentCutReason.Silence, completed.CutReason);
        Assert.AreEqual(300, completed.StartMs);
        Assert.AreEqual(1000, completed.EndMs);
    }

    [TestMethod]
    public void Push_WaitsForMinimumSegmentDurationBeforeCompletingOnSilence()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = 300,
            MinSegmentMs = 900,
            SoftMaxSegmentMs = 5000,
            HardMaxSegmentMs = 10000,
            OverlapMs = 0
        });

        segmenter.Push(Frame(0, speech: true), new VadDecision(true, 0.9f));
        segmenter.Push(Frame(100, speech: false), new VadDecision(false, 0.1f));
        segmenter.Push(Frame(200, speech: false), new VadDecision(false, 0.1f));
        var completed = segmenter.Push(Frame(300, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNull(completed);

        foreach (var frame in Frames(400, 4, speech: false))
        {
            completed = segmenter.Push(frame, new VadDecision(false, 0.1f));
            Assert.IsNull(completed);
        }

        completed = segmenter.Push(Frame(800, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(0, completed.StartMs);
        Assert.AreEqual(900, completed.EndMs);
    }

    [TestMethod]
    public void Push_RequiresConfiguredStartSpeechBeforeOpeningSegment()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            StartSpeechMs = 96,
            PreRollMs = 0,
            EndSilenceMs = 64,
            MinSegmentMs = 0,
            SoftMaxSegmentMs = 5000,
            HardMaxSegmentMs = 10000,
            OverlapMs = 0
        });

        Assert.IsNull(segmenter.Push(Frame(0, 32, speech: true), new VadDecision(true, 0.9f)));
        Assert.IsNull(segmenter.Push(Frame(32, 32, speech: false), new VadDecision(false, 0.1f)));

        Assert.IsNull(segmenter.Push(Frame(128, 32, speech: true), new VadDecision(true, 0.9f)));
        Assert.IsNull(segmenter.Push(Frame(160, 32, speech: true), new VadDecision(true, 0.9f)));
        Assert.IsNull(segmenter.Push(Frame(192, 32, speech: true), new VadDecision(true, 0.9f)));
        Assert.IsNull(segmenter.Push(Frame(224, 32, speech: false), new VadDecision(false, 0.1f)));

        var completed = segmenter.Push(Frame(256, 32, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(128, completed.StartMs);
        Assert.AreEqual(288, completed.EndMs);
    }

    [TestMethod]
    public void Push_IncludesConfiguredPreRollBeforeConfirmedSpeech()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            StartSpeechMs = 96,
            PreRollMs = 64,
            EndSilenceMs = 64,
            MinSegmentMs = 0,
            SoftMaxSegmentMs = 5000,
            HardMaxSegmentMs = 10000
        });

        segmenter.Push(Frame(0, 32, speech: false), new VadDecision(false, 0.1f));
        segmenter.Push(Frame(32, 32, speech: false), new VadDecision(false, 0.1f));
        segmenter.Push(Frame(64, 32, speech: false), new VadDecision(false, 0.1f));
        segmenter.Push(Frame(96, 32, speech: true), new VadDecision(true, 0.9f));
        segmenter.Push(Frame(128, 32, speech: true), new VadDecision(true, 0.9f));
        segmenter.Push(Frame(160, 32, speech: true), new VadDecision(true, 0.9f));
        segmenter.Push(Frame(192, 32, speech: false), new VadDecision(false, 0.1f));
        var completed = segmenter.Push(Frame(224, 32, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(32, completed.StartMs);
    }

    [TestMethod]
    public void Push_SoftMaxRequiresStableNonSpeechBreak()
    {
        var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            StartSpeechMs = 32,
            PreRollMs = 0,
            EndSilenceMs = 300,
            SoftBreakSilenceMs = 96,
            MinSegmentMs = 0,
            SoftMaxSegmentMs = 128,
            HardMaxSegmentMs = 1000
        });

        for (var time = 0; time < 128; time += 32)
        {
            segmenter.Push(Frame(time, 32, speech: true), new VadDecision(true, 0.9f));
        }

        Assert.IsNull(segmenter.Push(Frame(128, 32, speech: false), new VadDecision(false, 0.1f)));
        Assert.IsNull(segmenter.Push(Frame(160, 32, speech: true), new VadDecision(true, 0.9f)));
        Assert.IsNull(segmenter.Push(Frame(192, 32, speech: false), new VadDecision(false, 0.1f)));
        Assert.IsNull(segmenter.Push(Frame(224, 32, speech: false), new VadDecision(false, 0.1f)));
        var completed = segmenter.Push(Frame(256, 32, speech: false), new VadDecision(false, 0.1f));

        Assert.IsNotNull(completed);
        Assert.AreEqual(SpeechSegmentCutReason.SoftMax, completed.CutReason);
    }

    private static IEnumerable<PcmAudioFrame> Frames(long startMs, int count, bool speech)
    {
        for (var i = 0; i < count; i++)
        {
            yield return Frame(startMs + i * 100, speech);
        }
    }

    private static PcmAudioFrame Frame(long startMs, bool speech)
    {
        return Frame(startMs, 100, speech);
    }

    private static PcmAudioFrame Frame(long startMs, int durationMs, bool speech)
    {
        var sampleCount = 16000 * durationMs / 1000;
        var samples = speech ? Enumerable.Repeat<short>(1000, sampleCount).ToArray() : new short[sampleCount];
        return new PcmAudioFrame(startMs, durationMs, 16000, samples);
    }
}
