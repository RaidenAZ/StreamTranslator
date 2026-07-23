namespace StreamTranslator.Core.Configuration;

using StreamTranslator.Core.Translation;

public static class AppSettingsValidator
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto"
    };
    private static readonly HashSet<string> SupportedTranslationTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh-Hans", "en", "de", "fr", "ja"
    };

    public static IReadOnlyList<string> ValidateForStart(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.Asr.ApiKey))
        {
            errors.Add("请先配置 MiMo API Key。");
        }

        if (!Uri.TryCreate(settings.Asr.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            errors.Add("Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        }

        if (string.IsNullOrWhiteSpace(settings.Asr.Model))
        {
            errors.Add("ASR Model 不能为空。");
        }

        if (!SupportedLanguages.Contains(settings.Asr.Language))
        {
            errors.Add("识别语言暂时仅支持自动检测（auto）。");
        }

        if (settings.Asr.TimeoutMs is < 5000 or > 120000)
        {
            errors.Add("请求超时必须在 5000 到 120000 ms 之间。");
        }

        if (settings.Asr.MaxConcurrency is < 1 or > 4)
        {
            errors.Add("最大并发必须在 1 到 4 之间。");
        }

        if (!Enum.IsDefined(settings.Vad.EndpointMode))
        {
            errors.Add("断句策略无效，请重新选择。");
        }

        if (settings.Vad.EndSilenceMs is < 200 or > 800)
        {
            errors.Add("断句等待必须在 200 到 800 ms 之间。");
        }

        if (settings.Vad.HardMaxSegmentMs is < 6000 or > 20000 ||
            settings.Vad.HardMaxSegmentMs <= settings.Vad.SoftMaxSegmentMs ||
            settings.Vad.HardMaxSegmentMs <= settings.Vad.MinSegmentMs)
        {
            errors.Add("最长片段必须在 6000 到 20000 ms 之间，并大于最短片段和软分段上限。");
        }

        if (settings.Vad.StartSpeechMs <= 0 || settings.Vad.PreRollMs < 0 ||
            settings.Vad.SoftBreakSilenceMs <= 0 || settings.Vad.MinSegmentMs < 0 ||
            settings.Vad.SoftMaxSegmentMs <= 0 ||
            settings.Vad.OverlapMs < 0 || settings.Vad.OverlapMs >= settings.Vad.HardMaxSegmentMs)
        {
            errors.Add("VAD 分段参数无效，请恢复默认设置后重试。");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateTranslation(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();
        if (!settings.Translation.Enabled)
        {
            return errors;
        }

        if (!SupportedTranslationTargets.Contains(settings.Translation.TargetLanguage))
        {
            errors.Add("翻译目标语言无效，请重新选择。");
        }

        var profile = settings.Translation.ActiveProfile;
        if (profile is null)
        {
            errors.Add("启用翻译前，请选择一个模型配置。");
            return errors;
        }

        errors.AddRange(TranslationProfileRules.Validate(profile));
        return errors;
    }
}
