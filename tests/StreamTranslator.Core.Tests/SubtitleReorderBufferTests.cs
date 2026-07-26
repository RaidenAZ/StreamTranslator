using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SubtitleReorderBufferTests
{
    [TestMethod]
    public void Add_ReleasesItemsInSequenceOrder()
    {
        var buffer = new SubtitleReorderBuffer(firstSequence: 1);
        var second = Item(2, "第二句");
        var first = Item(1, "第一句");

        Assert.AreEqual(0, buffer.Add(second).Count);

        var released = buffer.Add(first);

        Assert.AreEqual(2, released.Count);
        Assert.AreEqual("第一句", released[0].SourceText);
        Assert.AreEqual("第二句", released[1].SourceText);
    }

    [TestMethod]
    public void Add_FailedPlaceholderReleasesLaterSuccessfulItems()
    {
        var buffer = new SubtitleReorderBuffer(firstSequence: 1);
        Assert.AreEqual(0, buffer.Add(Item(2, "第二句")).Count);

        var released = buffer.Add(Item(1, "识别失败", SubtitleStatus.Failed));

        Assert.AreEqual(2, released.Count);
        Assert.AreEqual(SubtitleStatus.Failed, released[0].Status);
        Assert.AreEqual("第二句", released[1].SourceText);
    }

    [TestMethod]
    public void Add_SkipsMissingSequenceAfterGapTimeout()
    {
        var timeProvider = new ManualReorderTimeProvider(DateTimeOffset.Parse("2026-07-26T12:00:00+08:00"));
        var buffer = new SubtitleReorderBuffer(firstSequence: 1, timeProvider, maxGapWait: TimeSpan.FromSeconds(30));

        // Sequence 1 never arrives; 2 and 3 wait behind the gap.
        Assert.AreEqual(0, buffer.Add(Item(2, "第二句")).Count);
        timeProvider.Advance(TimeSpan.FromSeconds(31));

        var released = buffer.Add(Item(3, "第三句"));

        Assert.AreEqual(2, released.Count);
        Assert.AreEqual("第二句", released[0].SourceText);
        Assert.AreEqual("第三句", released[1].SourceText);
        Assert.AreEqual(1, buffer.SkippedSequences);
    }

    [TestMethod]
    public void Add_SkipsMissingSequenceWhenPendingOverflows()
    {
        var buffer = new SubtitleReorderBuffer(firstSequence: 1, maxPending: 3);

        Assert.AreEqual(0, buffer.Add(Item(2, "第二句")).Count);
        Assert.AreEqual(0, buffer.Add(Item(3, "第三句")).Count);
        Assert.AreEqual(0, buffer.Add(Item(4, "第四句")).Count);

        var released = buffer.Add(Item(5, "第五句"));

        Assert.AreEqual(4, released.Count);
        Assert.AreEqual("第二句", released[0].SourceText);
        Assert.AreEqual(1, buffer.SkippedSequences);
    }

    [TestMethod]
    public void Add_ResetsGapTimerWhenGapCloses()
    {
        var timeProvider = new ManualReorderTimeProvider(DateTimeOffset.Parse("2026-07-26T12:00:00+08:00"));
        var buffer = new SubtitleReorderBuffer(firstSequence: 1, timeProvider, maxGapWait: TimeSpan.FromSeconds(30));

        Assert.AreEqual(0, buffer.Add(Item(2, "第二句")).Count);
        timeProvider.Advance(TimeSpan.FromSeconds(20));
        Assert.AreEqual(2, buffer.Add(Item(1, "第一句")).Count);

        // A new gap must get a fresh window instead of inheriting the old timer.
        Assert.AreEqual(0, buffer.Add(Item(4, "第四句")).Count);
        timeProvider.Advance(TimeSpan.FromSeconds(20));
        var released = buffer.Add(Item(3, "第三句"));

        Assert.AreEqual(2, released.Count);
        Assert.AreEqual(0, buffer.SkippedSequences);
    }

    private sealed class ManualReorderTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static SubtitleItem Item(long sequence, string text, SubtitleStatus status = SubtitleStatus.Final)
    {
        return new SubtitleItem
        {
            Sequence = sequence,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(1),
            SourceText = text,
            Status = status
        };
    }
}
