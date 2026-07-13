namespace StreamTranslator.Audio.Capture;

public sealed class SilenceGapFiller
{
    private readonly int _samplesPerFrame;
    private readonly int _frameDurationMs;
    private readonly int _triggerMs;
    private long? _lastDataMs;
    private long _emittedThroughMs;

    public SilenceGapFiller(int sampleRate, int frameDurationMs, int triggerMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameDurationMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(triggerMs, frameDurationMs);
        _samplesPerFrame = sampleRate * frameDurationMs / 1000;
        _frameDurationMs = frameDurationMs;
        _triggerMs = triggerMs;
    }

    public void MarkDataReceived(long elapsedMs)
    {
        _lastDataMs = elapsedMs;
        _emittedThroughMs = elapsedMs;
    }

    public int GetMissingSampleCount(long elapsedMs)
    {
        if (_lastDataMs is null || elapsedMs - _lastDataMs.Value < _triggerMs)
        {
            return 0;
        }

        var completeFrames = (elapsedMs - _emittedThroughMs) / _frameDurationMs;
        if (completeFrames <= 0)
        {
            return 0;
        }

        _emittedThroughMs += completeFrames * _frameDurationMs;
        return checked((int)completeFrames * _samplesPerFrame);
    }
}
