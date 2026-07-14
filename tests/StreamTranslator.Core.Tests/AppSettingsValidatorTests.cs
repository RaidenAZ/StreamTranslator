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
        Assert.IsTrue(errors.Any(error => error.Contains("auto、zh、en", StringComparison.Ordinal)));
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
}
