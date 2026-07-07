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
}
