using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamTranslator.Core.Configuration;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 6;
    public AudioSettings Audio { get; init; } = new();
    public VadSettings Vad { get; init; } = new();
    public AsrSettings Asr { get; init; } = new();
    public TranslationSettings Translation { get; init; } = new();
    public SubtitleWindowSettings SubtitleWindow { get; init; } = new();
    public HotkeySettings Hotkeys { get; init; } = new();
    public DiagnosticsSettings Diagnostics { get; init; } = new();
    public AppearanceSettings Appearance { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<AppThemeMode>))]
public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed record AppearanceSettings
{
    public AppThemeMode Theme { get; init; } = AppThemeMode.System;
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
    /// <summary>Active provider slot: "Mimo" | "Zhipu" | "Custom".</summary>
    public string ActiveProvider { get; init; } = "Mimo";
    public AsrSlotConfig Mimo { get; init; } = new();
    public AsrSlotConfig Zhipu { get; init; } = new();
    public AsrCustomSlotConfig Custom { get; init; } = new();
    public string Language { get; init; } = "auto";
    public int TimeoutMs { get; init; } = 30000;
    public int MaxConcurrency { get; init; } = 2;

    [JsonIgnore]
    public string ActiveApiKey => ActiveProvider switch
    {
        "Zhipu"  => Zhipu.ApiKey,
        "Custom" => Custom.ApiKey,
        _        => Mimo.ApiKey
    };

    [JsonIgnore]
    public string ActiveBaseUrl => ActiveProvider switch
    {
        "Zhipu"  => AsrProviderDefaults.ZhipuBaseUrl,
        "Custom" => Custom.BaseUrl,
        _        => AsrProviderDefaults.MimoBaseUrl
    };

    [JsonIgnore]
    public string ActiveModel => ActiveProvider switch
    {
        "Zhipu"  => AsrProviderDefaults.ZhipuModel,
        "Custom" => Custom.Model,
        _        => AsrProviderDefaults.MimoModel
    };

    /// <summary>
    /// Wire protocol type sent to the Python worker via the ASR_PROVIDER env var.
    /// "Mimo" uses chat-completion with inline base64 audio;
    /// "Whisper" uses the standard audio/transcriptions file-upload endpoint.
    /// </summary>
    [JsonIgnore]
    public string ActiveProviderType => ActiveProvider == "Mimo" ? "Mimo" : "Whisper";
}

/// <summary>API-key slot for a named ASR provider (MiMo or Zhipu).
/// The base URL and model are fixed constants for these providers.</summary>
public sealed record AsrSlotConfig
{
    public string ApiKey { get; init; } = "";
}

/// <summary>Fully-configurable slot for a user-defined ASR provider.</summary>
public sealed record AsrCustomSlotConfig
{
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>"Whisper" (standard audio/transcriptions) or "Mimo" (chat-completions).</summary>
    public string ProviderType { get; init; } = "Whisper";
}

/// <summary>Canonical base-URL and model-name constants for the two built-in ASR providers.</summary>
public static class AsrProviderDefaults
{
    public const string MimoBaseUrl = "https://api.xiaomimimo.com/v1";
    public const string MimoModel   = "mimo-v2.5-asr";
    public const string ZhipuBaseUrl = "https://open.bigmodel.cn/api/paas/v4";
    public const string ZhipuModel   = "glm-asr-2512";
}

public sealed record SubtitleWindowSettings
{
    public double FontSize { get; init; } = 18;
    public int MaxSubtitleItems { get; init; } = 2;
    public double Opacity { get; init; } = 0.72;
    public bool ClickThroughWhenLocked { get; init; } = true;
    // 悬浮窗上次的位置与宽度；null 表示尚未记录，首次显示居中。
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double? WindowWidth { get; init; }
}

public sealed record TranslationSettings
{
    public bool Enabled { get; init; }
    public string TargetLanguage { get; init; } = "zh-Hans";
    public Guid? ActiveProfileId { get; init; }
    public List<TranslationProfile> Profiles { get; init; } = [];
    /// <summary>
    /// 翻译积累上限（字符数）。TextSentenceAccumulator 在积累文本超过此阈值且
    /// 未找到句末边界时强制刷出翻译单元。范围 50–800，默认 350。
    /// </summary>
    public int SentenceAccumulationLimit { get; init; } = 350;

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
