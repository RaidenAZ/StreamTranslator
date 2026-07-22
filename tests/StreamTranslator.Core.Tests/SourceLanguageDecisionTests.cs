using StreamTranslator.Core.Translation;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SourceLanguageDecisionTests
{
    [DataTestMethod]
    [DataRow("zh", "zh-Hans", "欢迎来到直播。", true)]
    [DataRow("en", "en", "Welcome to the stream.", true)]
    [DataRow("zh", "en", "欢迎来到直播。", false)]
    [DataRow("en", "zh-Hans", "Welcome to the stream.", false)]
    [DataRow("auto", "zh-Hans", "欢迎来到今天的产品发布直播。", true)]
    [DataRow("auto", "en", "Welcome to today's product launch live stream.", true)]
    [DataRow("auto", "zh-Hans", "WWDC 发布了 iPhone updates", false)]
    [DataRow("auto", "en", "F1", false)]
    [DataRow("auto", "zh-Hans", "好", false)]
    [DataRow("auto", "en", "OK", false)]
    [DataRow("auto", "en", "9:30", false)]
    public void ShouldSkip_OnlySkipsHighConfidenceSameLanguage(
        string sourceLanguage,
        string targetLanguage,
        string text,
        bool expected)
    {
        Assert.AreEqual(expected, SourceLanguageDecision.ShouldSkip(sourceLanguage, targetLanguage, text));
    }
}
