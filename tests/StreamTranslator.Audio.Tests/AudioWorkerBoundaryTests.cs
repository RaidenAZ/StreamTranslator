using StreamTranslator.Audio.Encoding;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class AudioWorkerBoundaryTests
{
    [TestMethod]
    public void TranscribeRequest_CarriesDecodablePcm16WaveBase64()
    {
        var wav = WavEncoder.EncodePcm16Mono(Enumerable.Repeat<short>(1234, 16000).ToArray(), 16000);
        var request = WorkerRequest.Transcribe("seg-1", 1, 0, 1000, 16000, "zh", Convert.ToBase64String(wav));
        var json = WorkerJson.Serialize(request);
        var roundTrip = WorkerJson.Deserialize<WorkerRequest>(json);
        var decoded = Convert.FromBase64String(roundTrip!.AudioBase64!);

        Assert.AreEqual("wav", roundTrip.AudioFormat);
        Assert.AreEqual(16000, roundTrip.SampleRate);
        Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(decoded, 0, 4));
        Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(decoded, 8, 4));
    }

    [TestMethod]
    public void TenSecondSegment_RemainsWellBelowMimoBase64Limit()
    {
        var wav = WavEncoder.EncodePcm16Mono(new short[16000 * 10], 16000);
        var base64 = Convert.ToBase64String(wav);

        Assert.IsTrue(System.Text.Encoding.ASCII.GetByteCount(base64) < 10 * 1024 * 1024);
    }
}
