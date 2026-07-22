using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamTranslator.Core.Configuration;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 3;
    public AudioSettings Audio { get; init; } = new();
    public VadSettings Vad { get; init; } = new();
    public AsrSettings Asr { get; init; } = new();
    public TranslationSettings Translation { get; init; } = new();
    public SubtitleWindowSettings SubtitleWindow { get; init; } = new();
    public HotkeySettings Hotkeys { get; init; } = new();
    public DiagnosticsSettings Diagnostics { get; init; } = new();
}

public sealed record AudioSettings
{
    public string DeviceId { get; init; } = "default";
    public bool FollowDefaultDevice { get; init; } = true;
}

public sealed record VadSettings
{
    public VadEndpointMode EndpointMode { get; init; } = VadEndpointMode.Balanced;
    public int EndSilenceMs { get; init; } = 400;
    public int StartSpeechMs { get; init; } = 96;
    public int PreRollMs { get; init; } = 192;
    public int MinSegmentMs { get; init; } = 900;
    public int SoftBreakSilenceMs { get; init; } = 128;
    public int SoftMaxSegmentMs { get; init; } = 4000;
    public int HardMaxSegmentMs { get; init; } = 10000;
    public int OverlapMs { get; init; } = 600;
}

[JsonConverter(typeof(JsonStringEnumConverter<VadEndpointMode>))]
public enum VadEndpointMode
{
    LowLatency,
    Balanced,
    SentenceComplete,
    Fixed
}

public sealed record AsrSettings
{
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.xiaomimimo.com/v1";
    public string Model { get; init; } = "mimo-v2.5-asr";
    public string Language { get; init; } = "auto";
    public int TimeoutMs { get; init; } = 30000;
    public int MaxConcurrency { get; init; } = 2;
}

public sealed record SubtitleWindowSettings
{
    public double FontSize { get; init; } = 18;
    public int MaxSubtitleItems { get; init; } = 2;
    public double Opacity { get; init; } = 0.72;
    public bool ClickThroughWhenLocked { get; init; } = true;
}

public sealed record TranslationSettings
{
    public bool Enabled { get; init; }
    public string TargetLanguage { get; init; } = "zh-Hans";
    public Guid? ActiveProfileId { get; init; }
    public List<TranslationProfile> Profiles { get; init; } = [];

    [JsonIgnore]
    public TranslationProfile? ActiveProfile => ActiveProfileId is { } id
        ? Profiles.FirstOrDefault(profile => profile.Id == id)
        : null;

    [JsonIgnore]
    public bool IsEffectivelyEnabled => Enabled && ActiveProfile is not null;
}

public sealed record TranslationProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public TranslationServiceLocation Location { get; init; } = TranslationServiceLocation.Remote;
    public TranslationRequestCompatibility RequestCompatibility { get; init; } = TranslationRequestCompatibility.Standard;
    public JsonElement CustomExtraBody { get; init; } = JsonDocument.Parse("{}").RootElement.Clone();
    public int TimeoutMs { get; init; } = 10000;
    public int MaxConcurrency { get; init; } = 2;
    public string? ValidationFingerprint { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }
    public int? LastValidationLatencyMs { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TranslationServiceLocation>))]
public enum TranslationServiceLocation
{
    Local,
    Remote
}

[JsonConverter(typeof(JsonStringEnumConverter<TranslationRequestCompatibility>))]
public enum TranslationRequestCompatibility
{
    Standard,
    DeepSeek,
    QwenVllm,
    Custom
}

public sealed record HotkeySettings
{
    public bool Enabled { get; init; } = true;
    public string ToggleCaption { get; init; } = "Ctrl+Alt+S";
    public string ToggleWindow { get; init; } = "Ctrl+Alt+H";
    public string ToggleLock { get; init; } = "Ctrl+Alt+L";
}

public sealed record DiagnosticsSettings
{
    public bool Enabled { get; init; }
    public bool SaveSegmentAudio { get; init; }
    public bool SaveVadTimeline { get; init; }
}
