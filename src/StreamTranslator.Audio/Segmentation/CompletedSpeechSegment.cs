namespace StreamTranslator.Audio.Segmentation;

public sealed record CompletedSpeechSegment(
    long StartMs,
    long EndMs,
    int SampleRate,
    short[] Samples,
    SpeechSegmentCutReason CutReason,
    int OverlapMs);

public enum SpeechSegmentCutReason
{
    Silence,
    SoftMax,
    HardMax
}

