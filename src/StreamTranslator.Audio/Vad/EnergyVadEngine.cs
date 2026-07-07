namespace StreamTranslator.Audio.Vad;

public sealed class EnergyVadEngine : IVadEngine
{
    private readonly double _thresholdDb;

    public EnergyVadEngine(double thresholdDb = -42)
    {
        _thresholdDb = thresholdDb;
    }

    public VadDecision Analyze(ReadOnlySpan<short> pcm16Frame, int sampleRate)
    {
        if (pcm16Frame.IsEmpty)
        {
            return new VadDecision(false, 0);
        }

        var sumSquares = 0d;
        foreach (var sample in pcm16Frame)
        {
            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / pcm16Frame.Length);
        var db = rms <= 0 ? -100 : 20 * Math.Log10(rms);
        var probability = (float)Math.Clamp((db - _thresholdDb + 12) / 24, 0, 1);
        return new VadDecision(db >= _thresholdDb, probability);
    }

    public void Reset()
    {
    }

    public void Dispose()
    {
    }
}

