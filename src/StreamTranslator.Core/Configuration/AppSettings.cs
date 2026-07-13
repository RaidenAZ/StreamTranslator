namespace StreamTranslator.Core.Configuration;

public sealed record AppSettings
{
    public AudioSettings Audio { get; init; } = new();
    public VadSettings Vad { get; init; } = new();
    public AsrSettings Asr { get; init; } = new();
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
    public int EndSilenceMs { get; init; } = 300;
    public int StartSpeechMs { get; init; } = 96;
    public int PreRollMs { get; init; } = 192;
    public int MinSegmentMs { get; init; } = 900;
    public int SoftBreakSilenceMs { get; init; } = 128;
    public int SoftMaxSegmentMs { get; init; } = 4000;
    public int HardMaxSegmentMs { get; init; } = 10000;
    public int OverlapMs { get; init; } = 600;
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
    public double FontSize { get; init; } = 28;
    public int MaxLines { get; init; } = 2;
    public double Opacity { get; init; } = 0.72;
    public bool ClickThroughWhenLocked { get; init; } = true;
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
