using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.Core.Diagnostics;

public sealed record DiagnosticsSnapshot
{
    public string Version { get; init; } = "unknown";
    public string OperatingSystem { get; init; } = "unknown";
    public string DataDirectory { get; init; } = "unknown";
    public string AudioStatus { get; init; } = "unknown";
    public string VadStatus { get; init; } = "unknown";
    public string AsrWorkerStatus { get; init; } = "unknown";
    public string AsrApiStatus { get; init; } = "unknown";
    public string AsrModel { get; init; } = "unknown";
    public string AsrLanguage { get; init; } = "unknown";
    public int AsrMaxConcurrency { get; init; }
    public bool TranslationEnabled { get; init; }
    public string TranslationWorkerStatus { get; init; } = "unknown";
    public string TranslationApiStatus { get; init; } = "unknown";
    public TranslationProfile? TranslationProfile { get; init; }
    public string TranslationTargetLanguage { get; init; } = "unknown";
    public int TranslationQueueLength { get; init; }
    public int TranslationQueuePeak { get; init; }
    public string? TranslationRecentError { get; init; }
}

public static class DiagnosticsReportBuilder
{
    public static string Build(DiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var profile = snapshot.TranslationProfile;
        var endpoint = profile is null ? "none" : BuildSafeEndpoint(profile.BaseUrl);
        return $"""
            StreamTranslator {snapshot.Version}
            OS: {snapshot.OperatingSystem}
            DataDirectory: {snapshot.DataDirectory}
            AudioStatus: {snapshot.AudioStatus}
            VadStatus: {snapshot.VadStatus}
            AsrWorkerStatus: {snapshot.AsrWorkerStatus}
            AsrApiStatus: {snapshot.AsrApiStatus}
            AsrModel: {snapshot.AsrModel}
            AsrLanguage: {snapshot.AsrLanguage}
            AsrMaxConcurrency: {snapshot.AsrMaxConcurrency}
            TranslationEnabled: {snapshot.TranslationEnabled}
            TranslationWorkerStatus: {snapshot.TranslationWorkerStatus}
            TranslationApiStatus: {snapshot.TranslationApiStatus}
            TranslationProfile: {profile?.Name ?? "none"}
            TranslationLocation: {profile?.Location.ToString() ?? "none"}
            TranslationModel: {profile?.Model ?? "none"}
            TranslationCompatibility: {profile?.RequestCompatibility.ToString() ?? "none"}
            TranslationEndpoint: {endpoint}
            TranslationTargetLanguage: {snapshot.TranslationTargetLanguage}
            TranslationQueue: {snapshot.TranslationQueueLength}/{snapshot.TranslationQueuePeak}
            TranslationRecentError: {snapshot.TranslationRecentError ?? "none"}
            """;
    }

    private static string BuildSafeEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return "invalid";
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return $"{builder.Uri.AbsoluteUri.TrimEnd('/')}/chat/completions";
    }
}
