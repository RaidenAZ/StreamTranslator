using NAudio.Dsp;

namespace StreamTranslator.Audio.Capture;

public sealed class StreamingAudioNormalizer : IDisposable
{
    private readonly int _sourceSampleRate;
    private readonly int _channels;
    private readonly WdlResampler? _resampler;

    public StreamingAudioNormalizer(int sourceSampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _sourceSampleRate = sourceSampleRate;
        _channels = channels;

        if (sourceSampleRate != AudioNormalizer.TargetSampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(false, 0, true, 64, 32);
            _resampler.SetFeedMode(true);
            _resampler.SetRates(sourceSampleRate, AudioNormalizer.TargetSampleRate);
        }
    }

    public short[] ProcessFloat32Bytes(ReadOnlySpan<byte> buffer)
    {
        const int bytesPerFloat = 4;
        var sampleCount = buffer.Length / bytesPerFloat;
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            samples[index] = BitConverter.ToSingle(buffer.Slice(index * bytesPerFloat, bytesPerFloat));
        }

        return ProcessFloatSamples(samples);
    }

    public short[] ProcessFloatSamples(ReadOnlySpan<float> interleavedSamples)
    {
        var mono = MixToMono(interleavedSamples);
        if (_resampler is null)
        {
            return ConvertToPcm16(mono);
        }

        var prepared = _resampler.ResamplePrepare(mono.Length, 1, out var inputBuffer, out var inputOffset);
        if (prepared < mono.Length)
        {
            throw new InvalidOperationException("WDL resampler did not accept the complete input block.");
        }

        mono.CopyTo(inputBuffer.AsSpan(inputOffset, mono.Length));
        var outputCapacity = Math.Max(1, (int)Math.Ceiling(mono.Length *
            (double)AudioNormalizer.TargetSampleRate / _sourceSampleRate) + 256);
        var output = new float[outputCapacity];
        var outputCount = _resampler.ResampleOut(output, 0, mono.Length, outputCapacity, 1);
        return ConvertToPcm16(output.AsSpan(0, outputCount));
    }

    private float[] MixToMono(ReadOnlySpan<float> samples)
    {
        var frameCount = samples.Length / _channels;
        var mono = new float[frameCount];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var sum = 0f;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += samples[frameIndex * _channels + channel];
            }

            mono[frameIndex] = sum / _channels;
        }

        return mono;
    }

    private static short[] ConvertToPcm16(ReadOnlySpan<float> samples)
    {
        var pcm = new short[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            var clamped = Math.Clamp(samples[index], -1f, 1f);
            pcm[index] = clamped >= 1f ? short.MaxValue : (short)Math.Round(clamped * short.MaxValue);
        }

        return pcm;
    }

    public void Dispose()
    {
    }
}
