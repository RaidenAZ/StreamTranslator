using StreamTranslator.Audio.Capture;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class AudioNormalizerTests
{
    [TestMethod]
    public void ConvertFloat32ToMonoPcm16_AveragesStereoSamples()
    {
        var bytes = new byte[4 * 4];
        WriteFloat(bytes, 0, 0.5f);
        WriteFloat(bytes, 4, -0.5f);
        WriteFloat(bytes, 8, 1.0f);
        WriteFloat(bytes, 12, 1.0f);

        var pcm = AudioNormalizer.ConvertFloat32ToMonoPcm16(bytes, channels: 2);

        Assert.AreEqual(2, pcm.Length);
        Assert.AreEqual(0, pcm[0]);
        Assert.AreEqual(short.MaxValue, pcm[1]);
    }

    private static void WriteFloat(byte[] buffer, int offset, float value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
    }
}

