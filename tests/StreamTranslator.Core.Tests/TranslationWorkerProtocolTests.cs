using System.Text.Json;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Translation;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class TranslationWorkerProtocolTests
{
    [TestMethod]
    public void ConfigureRequest_SerializesProfileWithoutCommandLineConfiguration()
    {
        var profile = new TranslationProfile
        {
            Id = Guid.Parse("9a7a57da-5c95-4e44-9e3b-54795ae90998"),
            Name = "DeepSeek",
            BaseUrl = "https://api.example.com/v1",
            Model = "model-name",
            ApiKey = "secret",
            Location = TranslationServiceLocation.Remote,
            RequestCompatibility = TranslationRequestCompatibility.DeepSeek
        };

        var json = WorkerJson.Serialize(TranslationWorkerRequest.Configure("cfg-1", profile));

        StringAssert.Contains(json, "\"type\":\"configure\"");
        StringAssert.Contains(json, "\"apiKey\":\"secret\"");
        StringAssert.Contains(json, "\"requestCompatibility\":\"DeepSeek\"");
        StringAssert.Contains(json, "\"promptVersion\":\"translation-v1\"");
    }

    [TestMethod]
    public void TranslateRequest_PreservesRevisionIdentityAndContext()
    {
        var request = TranslationWorkerRequest.Translate(
            "tr-1", 42, "session:42", 2, "auto", "zh-Hans", "Hello",
            [new TranslationContextItem("session:41", "Welcome", "欢迎", DateTimeOffset.Parse("2026-07-14T12:00:00+08:00"))],
            DateTimeOffset.Parse("2026-07-14T12:00:03+08:00"));

        var json = WorkerJson.Serialize(request);

        StringAssert.Contains(json, "\"utteranceGroupId\":\"session:42\"");
        StringAssert.Contains(json, "\"sourceRevision\":2");
        StringAssert.Contains(json, "\"translatedText\":\"欢迎\"");
        Assert.IsFalse(json.Contains("audioBase64", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Response_DeserializesUsageWarningsAndErrorMetadata()
    {
        const string json = """
            {"id":"tr-1","type":"translate_result","ok":true,"translatedText":"你好","warningCodes":["think_block_removed"],"usage":{"promptTokens":10,"completionTokens":2,"totalTokens":12}}
            """;

        var response = WorkerJson.Deserialize<TranslationWorkerResponse>(json);

        Assert.IsNotNull(response);
        Assert.AreEqual("你好", response.TranslatedText);
        CollectionAssert.Contains(response.WarningCodes, "think_block_removed");
        Assert.AreEqual(12, response.Usage?.TotalTokens);
    }

    [TestMethod]
    public void ConfigureRequest_UsesResolvedCustomExtraBodyObject()
    {
        var profile = new TranslationProfile
        {
            Name = "Custom",
            BaseUrl = "http://127.0.0.1:8000/v1",
            Model = "model",
            Location = TranslationServiceLocation.Local,
            RequestCompatibility = TranslationRequestCompatibility.Custom,
            CustomExtraBody = JsonDocument.Parse("{\"seed\":42}").RootElement.Clone()
        };

        var json = WorkerJson.Serialize(TranslationWorkerRequest.Configure("cfg", profile));

        StringAssert.Contains(json, "\"customExtraBody\":{\"seed\":42}");
    }
}
