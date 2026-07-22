using System.Text.Json;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class TranslationConfigurationTests
{
    [TestMethod]
    public void ValidateProfile_RejectsUnsafeRemoteAndAmbiguousEndpoint()
    {
        var profile = Profile() with
        {
            Location = TranslationServiceLocation.Remote,
            BaseUrl = "http://user@example.com/v1/chat/completions?key=x"
        };

        var errors = TranslationProfileRules.Validate(profile);

        Assert.IsTrue(errors.Any(error => error.Contains("HTTPS", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(error => error.Contains("userinfo", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(errors.Any(error => error.Contains("query", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(errors.Any(error => error.Contains("chat/completions", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void BuildFinalEndpoint_NormalizesSlashWithoutAddingV1()
    {
        Assert.AreEqual(
            "http://127.0.0.1:8000/chat/completions",
            TranslationProfileRules.BuildFinalEndpoint("http://127.0.0.1:8000/"));
        Assert.AreEqual(
            "http://127.0.0.1:8000/v1/chat/completions",
            TranslationProfileRules.BuildFinalEndpoint("http://127.0.0.1:8000/v1"));
    }

    [DataTestMethod]
    [DataRow("http://127.0.0.1:8000/v1", TranslationServiceLocation.Local)]
    [DataRow("http://192.168.1.20:8000/v1", TranslationServiceLocation.Local)]
    [DataRow("https://api.example.com/v1", TranslationServiceLocation.Remote)]
    public void SuggestLocation_ClassifiesNetworkAddress(string baseUrl, TranslationServiceLocation expected)
    {
        Assert.AreEqual(expected, TranslationProfileRules.SuggestLocation(baseUrl));
    }

    [TestMethod]
    public void ResolveExtraBody_UsesCompatibilityTemplates()
    {
        Assert.AreEqual("{}", TranslationProfileRules.ResolveExtraBody(Profile()).GetRawText());
        Assert.AreEqual(
            "{\"thinking\":{\"type\":\"disabled\"}}",
            TranslationProfileRules.ResolveExtraBody(Profile() with
            {
                RequestCompatibility = TranslationRequestCompatibility.DeepSeek
            }).GetRawText());
        Assert.AreEqual(
            "{\"chat_template_kwargs\":{\"enable_thinking\":false}}",
            TranslationProfileRules.ResolveExtraBody(Profile() with
            {
                RequestCompatibility = TranslationRequestCompatibility.QwenVllm
            }).GetRawText());
    }

    [TestMethod]
    public void ValidateProfile_RejectsReservedCustomExtraBodyRecursivelyAtTopLevel()
    {
        var profile = Profile() with
        {
            RequestCompatibility = TranslationRequestCompatibility.Custom,
            CustomExtraBody = JsonDocument.Parse("{\"messages\":[],\"safe\":true}").RootElement.Clone()
        };

        var errors = TranslationProfileRules.Validate(profile);

        Assert.IsTrue(errors.Any(error => error.Contains("messages", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ValidationFingerprint_ChangesWhenCriticalConfigurationChanges()
    {
        var profile = Profile();

        Assert.AreNotEqual(
            TranslationProfileRules.CreateValidationFingerprint(profile),
            TranslationProfileRules.CreateValidationFingerprint(profile with { Model = "other-model" }));
    }

    private static TranslationProfile Profile() => new()
    {
        Id = Guid.Parse("9a7a57da-5c95-4e44-9e3b-54795ae90998"),
        Name = "Local model",
        BaseUrl = "http://127.0.0.1:8000/v1",
        Model = "model-name",
        Location = TranslationServiceLocation.Local
    };
}
