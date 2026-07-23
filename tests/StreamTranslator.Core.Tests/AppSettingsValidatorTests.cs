using StreamTranslator.Core.Configuration;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class AppSettingsValidatorTests
{
    [TestMethod]
    public void ValidateForStart_RejectsMissingApiKeyAndUnsupportedLanguage()
    {
        var settings = new AppSettings
        {
            Asr = new AsrSettings { ApiKey = "", Language = "ja" }
        };

        var errors = AppSettingsValidator.ValidateForStart(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("API Key", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(error => error.Contains("auto", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ValidateForStart_AcceptsSupportedConfiguration()
    {
        var settings = new AppSettings
        {
            Asr = new AsrSettings { ApiKey = "test-key", Language = "auto" }
        };

        Assert.AreEqual(0, AppSettingsValidator.ValidateForStart(settings).Count);
    }

    [TestMethod]
    public void ValidateForStart_RejectsUnknownVadEndpointMode()
    {
        var settings = new AppSettings
        {
            Asr = new AsrSettings { ApiKey = "test-key", Language = "auto" },
            Vad = new VadSettings { EndpointMode = (VadEndpointMode)99 }
        };

        var errors = AppSettingsValidator.ValidateForStart(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("断句策略", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ValidateForStart_RejectsForcedAsrLanguage()
    {
        var settings = new AppSettings
        {
            Asr = new AsrSettings { ApiKey = "test-key", Language = "en" }
        };

        var errors = AppSettingsValidator.ValidateForStart(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("自动检测", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(5000)]
    [DataRow(20001)]
    public void ValidateForStart_RejectsHardMaxSegmentOutsideSupportedRange(int hardMaxSegmentMs)
    {
        var settings = new AppSettings
        {
            Asr = new AsrSettings { ApiKey = "test-key", Language = "auto" },
            Vad = new VadSettings { HardMaxSegmentMs = hardMaxSegmentMs }
        };

        var errors = AppSettingsValidator.ValidateForStart(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("最长片段", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ValidateForStart_RejectsHardMaxSegmentNotAboveMinimumOrSoftMax()
    {
        var settings = new AppSettings
        {
            Asr = new AsrSettings { ApiKey = "test-key", Language = "auto" },
            Vad = new VadSettings
            {
                MinSegmentMs = 7000,
                SoftMaxSegmentMs = 8000,
                HardMaxSegmentMs = 8000
            }
        };

        var errors = AppSettingsValidator.ValidateForStart(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("最长片段", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ValidateTranslation_RequiresActiveValidProfileOnlyWhenEnabled()
    {
        var disabled = new AppSettings();
        Assert.AreEqual(0, AppSettingsValidator.ValidateTranslation(disabled).Count);

        var missing = disabled with { Translation = new TranslationSettings { Enabled = true } };
        Assert.IsTrue(AppSettingsValidator.ValidateTranslation(missing)
            .Any(error => error.Contains("模型配置", StringComparison.Ordinal)));

        var profile = new TranslationProfile
        {
            Name = "Remote",
            BaseUrl = "http://api.example.com/v1",
            Model = "model",
            Location = TranslationServiceLocation.Remote
        };
        var invalid = missing with
        {
            Translation = missing.Translation with
            {
                ActiveProfileId = profile.Id,
                Profiles = [profile]
            }
        };
        Assert.IsTrue(AppSettingsValidator.ValidateTranslation(invalid)
            .Any(error => error.Contains("HTTPS", StringComparison.Ordinal)));
    }
}
