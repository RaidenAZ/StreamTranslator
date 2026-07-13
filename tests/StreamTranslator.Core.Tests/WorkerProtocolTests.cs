using StreamTranslator.Core.Worker;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class WorkerProtocolTests
{
    [TestMethod]
    public void SerializeRequest_UsesCamelCaseJsonProtocol()
    {
        var request = WorkerRequest.Transcribe(
            id: "seg-000123",
            sequence: 123,
            startMs: 48120,
            endMs: 54240,
            sampleRate: 16000,
            language: "zh",
            audioBase64: "AAAA");

        var json = WorkerJson.Serialize(request);

        StringAssert.Contains(json, "\"type\":\"transcribe\"");
        StringAssert.Contains(json, "\"audioBase64\":\"AAAA\"");
        StringAssert.Contains(json, "\"sampleRate\":16000");
    }

    [TestMethod]
    public void DeserializeResponse_PreservesStructuredFailureMetadata()
    {
        const string json = """
            {"id":"seg-1","type":"error","ok":false,"sequence":1,"errorKind":"rate_limit","statusCode":429,"retryable":true}
            """;

        var response = WorkerJson.Deserialize<WorkerResponse>(json);

        Assert.IsNotNull(response);
        Assert.AreEqual("rate_limit", response.ErrorKind);
        Assert.AreEqual(429, response.StatusCode);
        Assert.IsTrue(response.Retryable);
    }
}
