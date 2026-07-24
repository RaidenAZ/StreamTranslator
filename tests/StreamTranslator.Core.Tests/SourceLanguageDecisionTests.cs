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
    [DataRow("auto", "de", "Willkommen zum heutigen Formel-1-Rennen.", true)]
    [DataRow("auto", "fr", "Bienvenue dans la diffusion en direct.", true)]
    [DataRow("auto", "ja", "今日のレース中継へようこそ。", true)]
    [DataRow("auto", "en", "Der Fahrer ist heute sehr schnell.", false)]
    [DataRow("auto", "en", "Le pilote est tres rapide aujourd'hui.", false)]
    [DataRow("auto", "zh-Hans", "今日のレース中継へようこそ。", false)]
    [DataRow("auto", "ja", "世界最高速度", false)]
    [DataRow("auto", "zh-Hans", "WWDC 发布了 iPhone updates", false)]
    [DataRow("auto", "de", "The driver is very fast today.", false)]
    [DataRow("auto", "fr", "The driver is very fast today.", false)]
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

    [TestMethod]
    public void Analyze_ReportsDetectedLanguageEvenWhenTargetDiffers()
    {
        var result = SourceLanguageDecision.Analyze(
            "auto",
            "en",
            "Der Fahrer ist heute sehr schnell.");

        Assert.IsFalse(result.ShouldSkip);
        Assert.AreEqual("de", result.DetectedLanguage);
        Assert.AreEqual(85, result.Confidence);
    }
}
