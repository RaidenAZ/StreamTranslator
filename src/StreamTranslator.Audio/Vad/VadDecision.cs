namespace StreamTranslator.Audio.Vad;

public readonly record struct VadDecision(bool IsSpeech, float Probability);

