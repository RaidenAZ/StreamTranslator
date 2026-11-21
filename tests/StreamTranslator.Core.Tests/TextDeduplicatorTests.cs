using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class TextDeduplicatorTests
{
    [TestMethod]
    public void MergeOverlap_AppendsOnlyNonOverlappingText()
    {
        var result = TextDeduplicator.MergeOverlap(
            "这个功能我们需要先把音频切成",
            "先把音频切成适合识别的小段");

        Assert.AreEqual("这个功能我们需要先把音频切成适合识别的小段", result.MergedText);
        Assert.AreEqual("适合识别的小段", result.AppendedText);
        Assert.IsTrue(result.Deduplicated);
    }

    [TestMethod]
    public void MergeOverlap_LeavesNewTextSeparateWhenThereIsNoOverlap()
    {
        var result = TextDeduplicator.MergeOverlap("今天直播开始了", "我们来看下一场比赛");

        Assert.AreEqual("我们来看下一场比赛", result.MergedText);
        Assert.AreEqual("我们来看下一场比赛", result.AppendedText);
        Assert.IsFalse(result.Deduplicated);
    }

    [TestMethod]
    public void MergeOverlap_DoesNotMergeVeryShortChineseOverlap()
    {
        var result = TextDeduplicator.MergeOverlap("今天直播到这里", "这里继续");

        Assert.AreEqual("这里继续", result.MergedText);
        Assert.IsFalse(result.Deduplicated);
    }

    [TestMethod]
    public void MergeOverlap_MergesEnglishWordOverlap()
    {
        var result = TextDeduplicator.MergeOverlap(
            "we need to split audio into",
            "audio into small chunks");

        Assert.AreEqual("we need to split audio into small chunks", result.MergedText);
        Assert.AreEqual("small chunks", result.AppendedText);
        Assert.IsTrue(result.Deduplicated);
    }

    [TestMethod]
    public void MergeOverlap_HandlesPunctuationAndCaseDifferences()
    {
        var result = TextDeduplicator.MergeOverlap(
            "we need to split audio into.",
            "Audio into small chunks today");

        Assert.IsTrue(result.Deduplicated);
        Assert.AreEqual("small chunks today", result.AppendedText);
        Assert.AreEqual("we need to split audio into small chunks today", result.MergedText);
    }

    [TestMethod]
    public void StripLeftContext_StripsAlignedPrefixWithPunctuationAndCaseVariance()
    {
        var result = TextDeduplicator.StripLeftContext(
            "Train Sim World Six will be coming on September 2025.",
            "coming on September 2025, with a top-notch selection of routes.");

        Assert.IsTrue(result.Matched);
        Assert.IsFalse(result.IsPureDuplicate);
        Assert.AreEqual("with a top-notch selection of routes.", result.StrippedText);
        Assert.AreEqual(0, result.SuggestedPreviousTailTrimLength);
    }

    [TestMethod]
    public void StripLeftContext_RepairsTruncatedBoundaryWord()
    {
        var previous = "Train Sim World Journey. We are introducing the free start.";
        var recognized = "World Journey. We are introducing the free starter pack next week.";

        var result = TextDeduplicator.StripLeftContext(previous, recognized);

        Assert.IsTrue(result.Matched);
        Assert.AreEqual("starter pack next week.", result.StrippedText);
        Assert.AreEqual("start.".Length, result.SuggestedPreviousTailTrimLength);
    }

    [TestMethod]
    public void StripLeftContext_HandlesChineseWithSingleCharVariance()
    {
        var previous = "所以今天我们主要来聊一下那个新的架构方案";
        var recognized = "来聊一下这个新的架构方案，它最大的优势是延迟降低了一半。";

        var result = TextDeduplicator.StripLeftContext(previous, recognized);

        Assert.IsTrue(result.Matched);
        Assert.AreEqual("它最大的优势是延迟降低了一半。", result.StrippedText);
    }

    [TestMethod]
    public void StripLeftContext_FlagsPureDuplicateWhenNoNewSpeech()
    {
        var result = TextDeduplicator.StripLeftContext(
            "今天我们就先聊到这里。",
            "我们就先聊到这里。");

        Assert.IsTrue(result.Matched);
        Assert.IsTrue(result.IsPureDuplicate);
        Assert.AreEqual("", result.StrippedText);
    }
}
