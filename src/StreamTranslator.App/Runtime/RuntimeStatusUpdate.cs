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
