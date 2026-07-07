namespace StreamTranslator.Audio.Segmentation;

public sealed record PcmAudioFrame(long StartMs, int DurationMs, int SampleRate, short[] Samples)
{
    public long EndMs => StartMs + DurationMs;
}

