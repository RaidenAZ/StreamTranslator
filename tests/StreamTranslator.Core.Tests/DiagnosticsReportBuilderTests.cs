using System.Text.Json;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Diagnostics;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class DiagnosticsReportBuilderTests
{
    [TestMethod]
    public void Build_IncludesTranslationRuntimeConfigurationAndRecentFailure()
    {
        using var extraBody = JsonDocument.Parse("{\"thinking\":{\"type\":\"disabled\"}}");
        var profile = new TranslationProfile
        {
            Name = "DeepSeek 远程",
            Model = "deepseek-v4-flash",
            ApiKey = "test-secret-api-key",
            BaseUrl = "https://api.example.test/v1",
            Location = TranslationServiceLocation.Remote,
            RequestCompatibility = TranslationRequestCompatibility.DeepSeek,
            CustomExtraBody = extraBody.RootElement.Clone()
        };
        var snapshot = new DiagnosticsSnapshot
        {
            Version = "V1.2",
            DataDirectory = "D:\\app\\data",
            AudioStatus = "捕获中",
            VadStatus = "运行中",
            AsrWorkerStatus = "已启动",
            AsrApiStatus = "正常",
            AsrModel = "mimo-v2.5-asr",
            AsrLanguage = "auto",
            AsrMaxConcurrency = 2,
            TranslationEnabled = true,
            TranslationWorkerStatus = "已启动",
            TranslationApiStatus = "错误: network",
            TranslationProfile = profile,
            TranslationTargetLanguage = "zh-Hans",
            TranslationQueueLength = 3,
            TranslationQueuePeak = 8,
            TranslationRecentError = "翻译失败（网络），原文字幕继续显示。"
        };

        var report = DiagnosticsReportBuilder.Build(snapshot);

        StringAssert.Contains(report, "TranslationEnabled: True");
        StringAssert.Contains(report, "TranslationWorkerStatus: 已启动");
        StringAssert.Contains(report, "TranslationApiStatus: 错误: network");
        StringAssert.Contains(report, "TranslationProfile: DeepSeek 远程");
        StringAssert.Contains(report, "TranslationCompatibility: DeepSeek");
        StringAssert.Contains(report, "TranslationTargetLanguage: zh-Hans");
        StringAssert.Contains(report, "TranslationQueue: 3/8");
        StringAssert.Contains(report, "TranslationRecentError: 翻译失败");
        StringAssert.Contains(report, "https://api.example.test/v1/chat/completions");
        Assert.IsFalse(report.Contains(profile.ApiKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Build_ReportsDisabledTranslationWithoutProfile()
    {
        var report = DiagnosticsReportBuilder.Build(new DiagnosticsSnapshot
        {
            Version = "V1.2",
            DataDirectory = "data",
            TranslationEnabled = false,
            TranslationWorkerStatus = "已关闭",
            TranslationApiStatus = "已关闭",
            TranslationTargetLanguage = "en"
        });

        StringAssert.Contains(report, "TranslationEnabled: False");
        StringAssert.Contains(report, "TranslationProfile: none");
        StringAssert.Contains(report, "TranslationQueue: 0/0");
    }

    [TestMethod]
    public void Build_StripsCredentialsAndQueryFromEndpointPreview()
    {
        var report = DiagnosticsReportBuilder.Build(new DiagnosticsSnapshot
        {
            TranslationProfile = new TranslationProfile
            {
                Name = "unsafe fixture",
                BaseUrl = "https://username:password@example.test/v1?api_key=url-secret"
            }
        });

        StringAssert.Contains(report, "https://example.test/v1/chat/completions");
        Assert.IsFalse(report.Contains("username", StringComparison.Ordinal));
        Assert.IsFalse(report.Contains("password", StringComparison.Ordinal));
        Assert.IsFalse(report.Contains("url-secret", StringComparison.Ordinal));
    }
}
