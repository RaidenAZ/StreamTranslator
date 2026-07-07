namespace StreamTranslator.Audio.Segmentation;

public sealed record SpeechSegmenterOptions
{
    public int EndSilenceMs { get; init; } = 300;
    public int StartSpeechMs { get; init; } = 96;
    public int MinSegmentMs { get; init; } = 900;
    public int SoftMaxSegmentMs { get; init; } = 4000;
    public int HardMaxSegmentMs { get; init; } = 10000;
    public int OverlapMs { get; init; } = 600;
}

