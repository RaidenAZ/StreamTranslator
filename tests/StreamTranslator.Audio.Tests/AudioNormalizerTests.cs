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

    [TestMethod]
    public void StreamingNormalizer_PreservesRateAcrossIrregularChunks()
    {
        using var normalizer = new StreamingAudioNormalizer(44100, channels: 1);
        var source = Enumerable.Range(0, 44100)
            .Select(index => (float)Math.Sin(index * 2 * Math.PI * 440 / 44100))
            .ToArray();
        var chunks = new[] { 997, 4096, 333, 8192, 127, 12000, 7000, 11355 };
        var output = new List<short>();
        var offset = 0;

        foreach (var chunkLength in chunks)
        {
            output.AddRange(normalizer.ProcessFloatSamples(source.AsSpan(offset, chunkLength)));
            offset += chunkLength;
        }

        Assert.AreEqual(source.Length, offset);
        Assert.IsTrue(Math.Abs(output.Count - 16000) <= 64, $"Unexpected output sample count: {output.Count}");
        Assert.IsTrue(output.Any(sample => sample != 0));
    }

    private static void WriteFloat(byte[] buffer, int offset, float value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
    }
}
