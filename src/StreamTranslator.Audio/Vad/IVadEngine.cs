namespace StreamTranslator.Audio.Vad;

public interface IVadEngine : IDisposable
{
    VadDecision Analyze(ReadOnlySpan<short> pcm16Frame, int sampleRate);
    void Reset();
}

