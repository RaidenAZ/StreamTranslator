namespace StreamTranslator.Core.Configuration;

public static class VadEndpointProfiles
{
    public static VadEndpointProfile Get(VadEndpointMode mode, int fixedEndSilenceMs)
    {
        if (fixedEndSilenceMs is < 200 or > 800)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedEndSilenceMs));
        }

        return mode switch
        {
            VadEndpointMode.LowLatency => new VadEndpointProfile(250, 200, 400, true),
            VadEndpointMode.Balanced => new VadEndpointProfile(400, 280, 600, true),
            VadEndpointMode.SentenceComplete => new VadEndpointProfile(600, 400, 800, true),
            VadEndpointMode.Fixed => new VadEndpointProfile(
                fixedEndSilenceMs,
                fixedEndSilenceMs,
                fixedEndSilenceMs,
                false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }
}

public sealed record VadEndpointProfile(
    int InitialEndSilenceMs,
    int MinimumEndSilenceMs,
    int MaximumEndSilenceMs,
    bool IsAdaptive);
