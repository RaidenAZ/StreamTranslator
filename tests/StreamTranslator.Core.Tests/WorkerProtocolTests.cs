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
}

