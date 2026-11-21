using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class UtteranceCompletenessAnalyzerTests
{
    [TestMethod]
    [DataRow("Because of the.", "dangling_function_word")]
    [DataRow("We are going to talk about the", "dangling_function_word")]
    [DataRow("Although the latency dropped,", "continuation_punctuation")]
    [DataRow("今天我们要聊一下这个新的架构，", "continuation_punctuation")]
    [DataRow("因为这套方案的。", "dangling_cjk_connector")]
    [DataRow("但是我们其实可以把", "dangling_cjk_connector")]
    [DataRow("This clause has no ending punctuation", "missing_terminal_punctuation")]
    public void Analyze_DetectsIncompleteUtterances(string text, string expectedReason)
    {
        var result = UtteranceCompletenessAnalyzer.Analyze(text);

        Assert.IsTrue(result.IsIncomplete, $"Expected incomplete for: {text}");
        Assert.AreEqual(expectedReason, result.Reason);
    }

    [TestMethod]
    [DataRow("Latency drops by half.")]
    [DataRow("Are you ready for the next release?")]
    [DataRow("这套方案的延迟降低了一半。")]
    [DataRow("大家晚上好！")]
    [DataRow("Okay")]
    [DataRow("好的")]
    [DataRow("好的。")]
    [DataRow("是的。")]
    public void Analyze_RecognizesCompleteUtterances(string text)
    {
        var result = UtteranceCompletenessAnalyzer.Analyze(text);

        Assert.IsFalse(result.IsIncomplete, $"Expected complete for: {text} (reason={result.Reason})");
    }

    [TestMethod]
    public void RecommendedContinuationWindowMs_WidensWindowForIncompleteUtterance()
    {
        var incomplete = UtteranceCompletenessAnalyzer.Analyze("Because of the");
        var complete = UtteranceCompletenessAnalyzer.Analyze("Everything works well.");

        Assert.AreEqual(
            UtteranceCompletenessAnalyzer.IncompleteContinuationWindowMs,
            UtteranceCompletenessAnalyzer.RecommendedContinuationWindowMs(incomplete, 600));
        Assert.AreEqual(
            600,
            UtteranceCompletenessAnalyzer.RecommendedContinuationWindowMs(complete, 600));
    }
}
