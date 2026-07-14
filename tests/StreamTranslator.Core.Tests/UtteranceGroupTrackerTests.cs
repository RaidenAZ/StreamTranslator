using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class UtteranceGroupTrackerTests
{
    [TestMethod]
    public void Assign_ChainsOnlyThreeAdjacentSegments()
    {
        var tracker = new UtteranceGroupTracker();

        var first = tracker.Assign(sequence: 1, startMs: 0, endMs: 3000, mergeWithPrevious: false);
        var second = tracker.Assign(sequence: 2, startMs: 3000, endMs: 6000, mergeWithPrevious: true);
        var third = tracker.Assign(sequence: 3, startMs: 6000, endMs: 9000, mergeWithPrevious: true);
        var fourth = tracker.Assign(sequence: 4, startMs: 9000, endMs: 11000, mergeWithPrevious: true);

        Assert.AreEqual("utt-000001", first.UtteranceGroupId);
        Assert.AreEqual(first.UtteranceGroupId, second.UtteranceGroupId);
        Assert.AreEqual(first.UtteranceGroupId, third.UtteranceGroupId);
        Assert.IsTrue(second.IsContinuation);
        Assert.IsTrue(third.IsContinuation);
        Assert.AreEqual(3, third.SegmentCount);
        Assert.AreEqual("utt-000004", fourth.UtteranceGroupId);
        Assert.IsFalse(fourth.IsContinuation);
    }

    [TestMethod]
    public void Assign_StartsNewGroupWhenCombinedSpanExceedsTwelveSeconds()
    {
        var tracker = new UtteranceGroupTracker();

        var first = tracker.Assign(sequence: 10, startMs: 1000, endMs: 8000, mergeWithPrevious: false);
        var second = tracker.Assign(sequence: 11, startMs: 8000, endMs: 13001, mergeWithPrevious: true);

        Assert.AreEqual("utt-000010", first.UtteranceGroupId);
        Assert.AreEqual("utt-000011", second.UtteranceGroupId);
        Assert.IsFalse(second.IsContinuation);
    }
}
