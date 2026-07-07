using StreamTranslator.Audio.Encoding;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class WavEncoderTests
{
    [TestMethod]
    public void EncodePcm16Mono_WritesValidRiffWaveHeader()
    {
        var wav = WavEncoder.EncodePcm16Mono([1, -1], 16000);

        Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.AreEqual("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.AreEqual("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.AreEqual(44 + 4, wav.Length);
        Assert.AreEqual(16000, BitConverter.ToInt32(wav, 24));
        Assert.AreEqual(1, BitConverter.ToInt16(wav, 22));
        Assert.AreEqual(16, BitConverter.ToInt16(wav, 34));
    }
}
