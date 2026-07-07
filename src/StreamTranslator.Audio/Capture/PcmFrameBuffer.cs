using StreamTranslator.Audio.Segmentation;

namespace StreamTranslator.Audio.Capture;

public sealed class PcmFrameBuffer
{
    private readonly int _sampleRate;
    private readonly int _frameDurationMs;
    private readonly int _samplesPerFrame;
    private readonly List<short> _buffer = [];
    private long _nextStartMs;

    public PcmFrameBuffer(int sampleRate, int frameDurationMs)
    {
        _sampleRate = sampleRate;
        _frameDurationMs = frameDurationMs;
        _samplesPerFrame = sampleRate * frameDurationMs / 1000;
    }

    public IReadOnlyList<PcmAudioFrame> Push(ReadOnlySpan<short> samples)
    {
        _buffer.AddRange(samples.ToArray());
        var frames = new List<PcmAudioFrame>();

        while (_buffer.Count >= _samplesPerFrame)
        {
            var frameSamples = _buffer.Take(_samplesPerFrame).ToArray();
            _buffer.RemoveRange(0, _samplesPerFrame);
            frames.Add(new PcmAudioFrame(_nextStartMs, _frameDurationMs, _sampleRate, frameSamples));
            _nextStartMs += _frameDurationMs;
        }

        return frames;
    }
}

