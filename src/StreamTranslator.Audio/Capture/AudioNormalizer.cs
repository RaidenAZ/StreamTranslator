namespace StreamTranslator.Audio.Capture;

public static class AudioNormalizer
{
    public const int TargetSampleRate = 16000;

    public static short[] ConvertFloat32ToMonoPcm16(ReadOnlySpan<byte> buffer, int channels)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be positive.");
        }

        const int bytesPerFloat = 4;
        var sampleCount = buffer.Length / bytesPerFloat / channels;
        var mono = new short[sampleCount];

        for (var frameIndex = 0; frameIndex < sampleCount; frameIndex++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frameIndex * channels + channel) * bytesPerFloat;
                sum += BitConverter.ToSingle(buffer.Slice(offset, bytesPerFloat));
            }

            mono[frameIndex] = FloatToPcm16(sum / channels);
        }

        return mono;
    }

    public static short[] ConvertFloatSamplesToMonoPcm16(ReadOnlySpan<float> samples, int channels)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be positive.");
        }

        var frameCount = samples.Length / channels;
        var mono = new short[frameCount];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += samples[frameIndex * channels + channel];
            }

            mono[frameIndex] = FloatToPcm16(sum / channels);
        }

        return mono;
    }

    public static short[] ResampleLinear(ReadOnlySpan<short> source, int sourceSampleRate, int targetSampleRate = TargetSampleRate)
    {
        if (sourceSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
        }

        if (sourceSampleRate == targetSampleRate || source.Length == 0)
        {
            return source.ToArray();
        }

        var targetLength = (int)Math.Round(source.Length * (double)targetSampleRate / sourceSampleRate);
        var target = new short[targetLength];
        var ratio = (double)sourceSampleRate / targetSampleRate;

        for (var i = 0; i < target.Length; i++)
        {
            var sourcePosition = i * ratio;
            var left = (int)Math.Floor(sourcePosition);
            var right = Math.Min(left + 1, source.Length - 1);
            var fraction = sourcePosition - left;
            var value = source[left] + (source[right] - source[left]) * fraction;
            target[i] = ClampToPcm16(value);
        }

        return target;
    }

    private static short FloatToPcm16(float value)
    {
        var clamped = Math.Clamp(value, -1f, 1f);
        return clamped >= 1f
            ? short.MaxValue
            : (short)Math.Round(clamped * short.MaxValue);
    }

    private static short ClampToPcm16(double value)
    {
        if (value > short.MaxValue)
        {
            return short.MaxValue;
        }

        if (value < short.MinValue)
        {
            return short.MinValue;
        }

        return (short)Math.Round(value);
    }
}
