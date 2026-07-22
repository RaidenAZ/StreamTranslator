using System.Text.Json;
using System.Text.Json.Serialization;
using StreamTranslator.Core.Configuration;

namespace StreamTranslator.Core.Translation;

public sealed record TranslationWorkerRequest
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public TranslationWorkerProfile? Profile { get; init; }
    public string? PromptVersion { get; init; }
    public long? Sequence { get; init; }
    public string? UtteranceGroupId { get; init; }
    public int? SourceRevision { get; init; }
    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }
    public string? SourceText { get; init; }
    public IReadOnlyList<TranslationContextItem>? Context { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public bool IsConnectionTest { get; init; }

    public static TranslationWorkerRequest Configure(string id, TranslationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new TranslationWorkerRequest
        {
            Id = id,
            Type = TranslationWorkerMessageTypes.Configure,
            PromptVersion = TranslationPrompt.Version,
            Profile = new TranslationWorkerProfile
            {
                ProfileId = profile.Id.ToString(),
                BaseUrl = TranslationProfileRules.NormalizeBaseUrl(profile.BaseUrl),
                Model = profile.Model,
                ApiKey = profile.ApiKey,
                RequestCompatibility = profile.RequestCompatibility,
                CustomExtraBody = profile.CustomExtraBody,
                TimeoutMs = profile.TimeoutMs,
                MaxConcurrency = profile.MaxConcurrency
            }
        };
    }

    public static TranslationWorkerRequest Translate(
        string id,
        long sequence,
        string utteranceGroupId,
        int sourceRevision,
        string sourceLanguage,
        string targetLanguage,
        string sourceText,
        IReadOnlyList<TranslationContextItem> context,
        DateTimeOffset createdAt,
        bool isConnectionTest = false)
    {
        return new TranslationWorkerRequest
        {
            Id = id,
            Type = TranslationWorkerMessageTypes.Translate,
            Sequence = sequence,
            UtteranceGroupId = utteranceGroupId,
            SourceRevision = sourceRevision,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            SourceText = sourceText,
            Context = context,
            CreatedAt = createdAt,
            IsConnectionTest = isConnectionTest
        };
    }

    public static TranslationWorkerRequest Shutdown(string id) => new()
    {
        Id = id,
        Type = TranslationWorkerMessageTypes.Shutdown
    };
}

public sealed record TranslationWorkerProfile
{
    public required string ProfileId { get; init; }
    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
    public string ApiKey { get; init; } = "";
    public TranslationRequestCompatibility RequestCompatibility { get; init; }
    public JsonElement CustomExtraBody { get; init; }
    public int TimeoutMs { get; init; }
    public int MaxConcurrency { get; init; }
}

public sealed record TranslationContextItem(
    string UtteranceGroupId,
    string SourceText,
    string? TranslatedText,
    [property: JsonIgnore] DateTimeOffset GeneratedAt);

public sealed record TranslationWorkerResponse
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public bool Ok { get; init; }
    public bool Configured { get; init; }
    public string? ProfileId { get; init; }
    public string? FinalEndpoint { get; init; }
    public long? Sequence { get; init; }
    public string? UtteranceGroupId { get; init; }
    public int? SourceRevision { get; init; }
    public string? TargetLanguage { get; init; }
    public string? TranslatedText { get; init; }
    public int? LatencyMs { get; init; }
    public string[] WarningCodes { get; init; } = [];
    public TranslationUsage? Usage { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorKind { get; init; }
    public int? StatusCode { get; init; }
    public bool Retryable { get; init; }
}

public sealed record TranslationUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}

public static class TranslationWorkerMessageTypes
{
    public const string Configure = "configure";
    public const string Configured = "configured";
    public const string Translate = "translate";
    public const string TranslateResult = "translate_result";
    public const string Shutdown = "shutdown";
}

public static class TranslationPrompt
{
    public const string Version = "translation-v1";
}
