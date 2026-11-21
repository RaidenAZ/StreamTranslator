namespace StreamTranslator.App.Runtime;

/// <summary>
/// 运行链路状态的归属类别。UI 按类别更新对应的状态格，
/// 不再依赖状态文案中的关键字匹配。
/// </summary>
public enum RuntimeStatusCategory
{
    General,
    AudioCapture,
    SpeechDetection,
    AsrWorker,
    AsrApi
}

public sealed record RuntimeStatusUpdate(RuntimeStatusCategory Category, string Message);

/// <summary>
/// 悬浮窗声学与识别活动状态，用于驱动底部 2px 呼吸指示条，
/// 在 VAD 切段与 API 返回的静默间隙向用户提供无打扰的存活反馈。
/// </summary>
public enum SubtitleActivityState
{
    /// <summary>未运行或当前静默无任务。</summary>
    Idle,
    /// <summary>VAD 检测到人声，正在监听并缓存片段。</summary>
    ListeningSpeech,
    /// <summary>已有语音片段送往 ASR 识别或等待翻译补齐。</summary>
    Processing
}

public readonly record struct SubtitleActivitySnapshot(
    SubtitleActivityState State,
    double AudioLevel,
    int ActiveAsrRequests);
