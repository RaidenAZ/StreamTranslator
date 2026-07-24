using StreamTranslator.Core.Translation;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class TranslationIssueTrackerTests
{
    [TestMethod]
    public void Apply_FailureProducesUserVisibleSummaryWithoutBlockingSourceSubtitle()
    {
        var tracker = new TranslationIssueTracker();

        var state = tracker.Apply(new TranslationTaskStatusUpdate(
            "group-1",
            1,
            "translation_failed",
            "network",
            DateTimeOffset.UtcNow));

        Assert.IsTrue(state.HasIssue);
        StringAssert.Contains(state.Summary, "翻译失败");
        StringAssert.Contains(state.Summary, "网络");
        StringAssert.Contains(state.Summary, "原文字幕继续显示");
        Assert.AreEqual(1, state.ConsecutiveFailures);
    }

    [TestMethod]
    public void Apply_RepeatedFailuresExposeCountAndSuccessClearsIssue()
    {
        var tracker = new TranslationIssueTracker();
        var failure = new TranslationTaskStatusUpdate(
            "group-1",
            1,
            "translation_failed",
            "timeout",
            DateTimeOffset.UtcNow);

        tracker.Apply(failure);
        var repeated = tracker.Apply(failure with { SourceRevision = 2 });
        var recovered = tracker.MarkSuccess();

        Assert.AreEqual(2, repeated.ConsecutiveFailures);
        StringAssert.Contains(repeated.Summary, "连续失败 2 次");
        Assert.IsFalse(recovered.HasIssue);
        Assert.AreEqual(0, recovered.ConsecutiveFailures);
    }

    [TestMethod]
    public void Apply_IgnoresNonFailureStatuses()
    {
        var tracker = new TranslationIssueTracker();

        var state = tracker.Apply(new TranslationTaskStatusUpdate(
            "group-1",
            1,
            "translation_skipped_same_language",
            null,
            DateTimeOffset.UtcNow));

        Assert.IsFalse(state.HasIssue);
        Assert.AreEqual(0, state.ConsecutiveFailures);
    }
}
