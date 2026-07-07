namespace StreamTranslator.Audio.Encoding;

public static class WavEncoder
{
    public static byte[] EncodePcm16Mono(ReadOnlySpan<short> samples, int sampleRate)
    {
        const short channelCount = 1;
        const short bitsPerSample = 16;
        const short bytesPerSample = bitsPerSample / 8;

        var dataSize = samples.Length * bytesPerSample;
        var buffer = new byte[44 + dataSize];

        WriteAscii(buffer, 0, "RIFF");
        WriteInt32(buffer, 4, 36 + dataSize);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        WriteInt32(buffer, 16, 16);
        WriteInt16(buffer, 20, 1);
        WriteInt16(buffer, 22, channelCount);
        WriteInt32(buffer, 24, sampleRate);
        WriteInt32(buffer, 28, sampleRate * channelCount * bytesPerSample);
        WriteInt16(buffer, 32, channelCount * bytesPerSample);
        WriteInt16(buffer, 34, bitsPerSample);
        WriteAscii(buffer, 36, "data");
        WriteInt32(buffer, 40, dataSize);

        for (var i = 0; i < samples.Length; i++)
        {
            WriteInt16(buffer, 44 + i * 2, samples[i]);
        }

        return buffer;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            buffer[offset + i] = (byte)value[i];
        }
    }

    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
        buffer[offset + 2] = (byte)((value >> 16) & 0xff);
        buffer[offset + 3] = (byte)((value >> 24) & 0xff);
    }
}

