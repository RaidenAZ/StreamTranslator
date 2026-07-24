namespace StreamTranslator.Core.Translation;

public sealed class TranslationIssueTracker
{
    private readonly object _stateLock = new();
    private int _consecutiveFailures;
    private TranslationIssueState _state = TranslationIssueState.Healthy;

    public TranslationIssueState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public TranslationIssueState Apply(TranslationTaskStatusUpdate update)
    {
        lock (_stateLock)
        {
            if (!IsUserVisibleFailure(update.Status))
            {
                return _state;
            }

            _consecutiveFailures++;
            var summary = BuildSummary(update.Status, update.ErrorKind, _consecutiveFailures);
            _state = new TranslationIssueState(
                true,
                summary,
                update.ErrorKind,
                _consecutiveFailures);
            return _state;
        }
    }

    public TranslationIssueState MarkSuccess()
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _state = TranslationIssueState.Healthy;
            return _state;
        }
    }

    private static bool IsUserVisibleFailure(string status)
    {
        return status is
            "translation_failed" or
            "translation_worker_crash" or
            "translation_worker_unavailable" or
            "translation_dropped_circuit_open" or
            "translation_dropped_backpressure" or
            "translation_dropped_expired" or
            "translation_disabled_fatal_error" or
            "translation_disabled_worker_crash" or
            "translation_disabled_worker_restart_failed";
    }

    private static string BuildSummary(string status, string? errorKind, int consecutiveFailures)
    {
        var suffix = $"连续失败 {consecutiveFailures} 次；原文字幕继续显示。";
        return status switch
        {
            "translation_dropped_backpressure" => $"翻译队列已满，部分译文未生成；{suffix}",
            "translation_dropped_expired" => $"翻译等待超时，部分译文未生成；{suffix}",
            "translation_dropped_circuit_open" => $"翻译接口暂时暂停请求；{suffix}",
            "translation_worker_crash" or "translation_worker_unavailable" or
                "translation_disabled_worker_crash" or "translation_disabled_worker_restart_failed" =>
                $"翻译进程不可用；{suffix}",
            "translation_disabled_fatal_error" => $"翻译配置或接口不可用；{suffix}",
            _ => $"翻译失败（{FormatErrorKind(errorKind)}）；{suffix}"
        };
    }

    private static string FormatErrorKind(string? errorKind)
    {
        return errorKind?.ToLowerInvariant() switch
        {
            "authentication" => "认证",
            "configuration" => "配置",
            "endpoint_not_found" => "接口地址",
            "invalid_request" => "请求参数",
            "invalid_response" => "响应格式",
            "model_not_found" => "模型不存在",
            "network" => "网络",
            "protocol" => "协议",
            "rate_limit" => "限流",
            "server" => "服务端",
            "timeout" => "超时",
            "worker" => "进程",
            _ => string.IsNullOrWhiteSpace(errorKind) ? "未知" : errorKind
        };
    }
}

public readonly record struct TranslationIssueState(
    bool HasIssue,
    string Summary,
    string? ErrorKind,
    int ConsecutiveFailures)
{
    public static TranslationIssueState Healthy => new(false, "", null, 0);
}
