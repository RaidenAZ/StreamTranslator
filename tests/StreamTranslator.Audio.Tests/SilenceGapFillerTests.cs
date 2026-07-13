using StreamTranslator.Audio.Capture;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class SilenceGapFillerTests
{
    [TestMethod]
    public void GetMissingSampleCount_FillsOnlyCompleteFramesAfterTrigger()
    {
        var filler = new SilenceGapFiller(sampleRate: 16000, frameDurationMs: 32, triggerMs: 128);
        filler.MarkDataReceived(elapsedMs: 100);

        Assert.AreEqual(0, filler.GetMissingSampleCount(elapsedMs: 220));
        Assert.AreEqual(2048, filler.GetMissingSampleCount(elapsedMs: 228));
        Assert.AreEqual(512, filler.GetMissingSampleCount(elapsedMs: 260));
        Assert.AreEqual(0, filler.GetMissingSampleCount(elapsedMs: 260));
    }

    [TestMethod]
    public void MarkDataReceived_ResetsSyntheticTimeline()
    {
        var filler = new SilenceGapFiller(sampleRate: 16000, frameDurationMs: 32, triggerMs: 128);
        filler.MarkDataReceived(0);
        Assert.AreEqual(2048, filler.GetMissingSampleCount(128));

        filler.MarkDataReceived(200);

        Assert.AreEqual(0, filler.GetMissingSampleCount(300));
        Assert.AreEqual(2048, filler.GetMissingSampleCount(328));
    }
}
