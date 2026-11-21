namespace StreamTranslator.Audio.Encoding;

/// <summary>
/// Prepares PCM16 mono speech clips before WAV encoding: trims excessive dead
/// trailing silence, applies short edge fades to avoid hard-cut clicks, and
/// locates quiet acoustic boundaries for left-context slicing.
/// </summary>
public static class AudioClipPreparer
{
    public const int DefaultMaxTrailingSilenceMs = 160;
    public const int DefaultFadeMs = 10;
    private const int MinRetainedAudioMs = 200;

    public static short[] PrepareForAsr(
        short[] samples,
        int sampleRate,
        int trailingSilenceMs,
        int maxTrailingSilenceMs = DefaultMaxTrailingSilenceMs,
        int fadeMs = DefaultFadeMs)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var trimmed = TrimTrailingSilence(samples, sampleRate, trailingSilenceMs, maxTrailingSilenceMs);
        ApplyEdgeFadeInPlace(trimmed, sampleRate, fadeMs);
        return trimmed;
    }

    public static short[] TrimTrailingSilence(
        short[] samples,
        int sampleRate,
        int trailingSilenceMs,
        int maxTrailingSilenceMs = DefaultMaxTrailingSilenceMs)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var trimmedLength = ComputeTrimmedSampleCount(
            samples.Length,
            sampleRate,
            trailingSilenceMs,
            maxTrailingSilenceMs);

        var result = new short[trimmedLength];
        if (trimmedLength > 0)
        {
            Array.Copy(samples, 0, result, 0, trimmedLength);
        }

        return result;
    }

    public static int ComputeTrimmedSampleCount(
        int totalSamples,
        int sampleRate,
        int trailingSilenceMs,
        int maxTrailingSilenceMs = DefaultMaxTrailingSilenceMs)
    {
        if (totalSamples <= 0 || maxTrailingSilenceMs < 0 || trailingSilenceMs <= maxTrailingSilenceMs)
        {
            return Math.Max(0, totalSamples);
        }

        var excessMs = trailingSilenceMs - maxTrailingSilenceMs;
        var trimSamples = (int)Math.Min(totalSamples, (long)excessMs * sampleRate / 1000);
        var minKeepSamples = Math.Min(totalSamples, sampleRate * MinRetainedAudioMs / 1000);
        return Math.Max(minKeepSamples, totalSamples - trimSamples);
    }

    public static void ApplyEdgeFadeInPlace(short[] samples, int sampleRate, int fadeMs = DefaultFadeMs)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0 || sampleRate <= 0 || fadeMs <= 0)
        {
            return;
        }

        var fadeSamples = Math.Min(samples.Length / 2, sampleRate * fadeMs / 1000);
        if (fadeSamples <= 1)
        {
            return;
        }

        for (var i = 0; i < fadeSamples; i++)
        {
            var gain = (double)i / fadeSamples;
            samples[i] = (short)Math.Round(samples[i] * gain);
            var tailIndex = samples.Length - 1 - i;
            samples[tailIndex] = (short)Math.Round(samples[tailIndex] * gain);
        }
    }

    /// <summary>
    /// Searches around <paramref name="targetOffsetSamples"/> within ±<paramref name="searchRadiusMs"/>
    /// for the start of the quietest 32ms energy window so left-context audio starts cleanly
    /// between words rather than cutting across a high-energy vowel.
    /// </summary>
    public static int FindLowEnergySplitOffset(
        ReadOnlySpan<short> samples,
        int sampleRate,
        int targetOffsetSamples,
        int searchRadiusMs = 250)
    {
        if (samples.IsEmpty || sampleRate <= 0)
        {
            return 0;
        }

        var clampedTarget = Math.Clamp(targetOffsetSamples, 0, samples.Length);
        var windowSamples = Math.Max(1, sampleRate * 32 / 1000);
        var stepSamples = Math.Max(1, sampleRate * 16 / 1000);
        var radiusSamples = Math.Max(0, sampleRate * searchRadiusMs / 1000);

        if (samples.Length <= windowSamples || radiusSamples < stepSamples)
        {
            return clampedTarget;
        }

        var minOffset = Math.Max(0, clampedTarget - radiusSamples);
        var maxOffset = Math.Min(samples.Length - windowSamples, clampedTarget + radiusSamples);
        if (maxOffset <= minOffset)
        {
            return clampedTarget;
        }

        var bestOffset = clampedTarget;
        var bestEnergy = double.PositiveInfinity;
        var targetEnergy = ComputeMeanSquareEnergy(
            samples.Slice(Math.Min(clampedTarget, samples.Length - windowSamples), windowSamples));

        for (var offset = minOffset; offset <= maxOffset; offset += stepSamples)
        {
            var energy = ComputeMeanSquareEnergy(samples.Slice(offset, windowSamples));
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestOffset = offset;
            }
        }

        // Only move away from the target if the discovered valley is noticeably quieter.
        if (bestEnergy >= targetEnergy * 0.85)
        {
            return clampedTarget;
        }

        return bestOffset;
    }

    private static double ComputeMeanSquareEnergy(ReadOnlySpan<short> window)
    {
        if (window.IsEmpty)
        {
            return 0;
        }

        var sum = 0d;
        for (var i = 0; i < window.Length; i++)
        {
            var normalized = window[i] / 32768d;
            sum += normalized * normalized;
        }

        return sum / window.Length;
    }
}
