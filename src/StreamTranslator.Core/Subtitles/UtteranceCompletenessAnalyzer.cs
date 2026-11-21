namespace StreamTranslator.Core.Subtitles;

/// <summary>
/// Lightweight, zero-dependency completeness classifier for ASR transcripts (Layer C1).
/// Uses punctuation and trailing clause-connector heuristics to detect utterances that
/// were likely cut mid-sentence so the runtime can widen merge windows, trigger audio
/// concatenation re-recognition (Layer B1), and feed semantic pressure back to the
/// adaptive endpoint controller.
/// </summary>
public static class UtteranceCompletenessAnalyzer
{
    public const int IncompleteContinuationWindowMs = 1400;

    private static readonly HashSet<char> ContinuationPunctuation =
    [
        ',', '，', '、', ';', '；', ':', '：', '-', '—', '–'
    ];

    private static readonly HashSet<char> TerminalPunctuation =
    [
        '.', '!', '?', '。', '！', '？', '…'
    ];

    private static readonly HashSet<char> ClosingWrappers =
    [
        '"', '\'', '”', '’', '」', '』', '》', '）', ')', ']', '】'
    ];

    private static readonly HashSet<string> DanglingEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "to", "of", "for", "with", "in", "on", "at", "by", "from",
        "into", "onto", "about", "through", "during", "before", "after", "between",
        "that", "which", "who", "whom", "whose", "where", "when", "while", "if",
        "because", "although", "though", "unless", "until", "whether",
        "and", "or", "but", "so", "nor", "yet", "as", "than",
        "my", "your", "his", "her", "its", "our", "their", "this", "these", "those",
        "very", "really", "also", "just", "actually", "basically",
        "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "will", "would", "can", "could", "should", "must", "might", "may"
    };

    private static readonly string[] DanglingCjkSuffixes =
    [
        "也就是说", "但是", "不过", "因为", "所以", "如果", "虽然", "而且", "并且",
        "或者", "还是", "然后", "接着", "就是", "比如", "例如", "关于", "对于",
        "通过", "根据", "以及", "包括", "只要", "只有", "即使", "既然", "无论",
        "把", "被", "给", "向", "从", "在", "的", "地", "得", "和", "与", "跟",
        "或", "而", "但", "却", "并", "让", "使", "将"
    ];

    private static readonly HashSet<string> CompleteShortInterjections = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "okay", "yes", "yeah", "yep", "no", "nope", "thanks", "thank you",
        "bye", "goodbye", "hello", "hi", "hey", "sure", "right", "exactly",
        "great", "nice", "cool", "awesome", "welcome", "please", "sorry",
        "好的", "好", "行", "可以", "是的", "对", "对的", "嗯", "哦", "谢谢", "拜拜",
        "你好", "没问题", "明白", "了解", "知道了", "当然", "可以的", "没事", "没关系"
    };

    public static UtteranceCompletenessResult Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new UtteranceCompletenessResult(IsIncomplete: false, Reason: "empty");
        }

        var trimmed = text.Trim();
        var coreEnd = trimmed.Length - 1;
        while (coreEnd >= 0 && ClosingWrappers.Contains(trimmed[coreEnd]))
        {
            coreEnd--;
        }

        if (coreEnd < 0)
        {
            return new UtteranceCompletenessResult(IsIncomplete: false, Reason: "empty");
        }

        var lastChar = trimmed[coreEnd];
        if (ContinuationPunctuation.Contains(lastChar))
        {
            return new UtteranceCompletenessResult(IsIncomplete: true, Reason: "continuation_punctuation");
        }

        var hasTerminalPunctuation = TerminalPunctuation.Contains(lastChar);
        var bodyEnd = coreEnd;
        while (bodyEnd >= 0 && (TerminalPunctuation.Contains(trimmed[bodyEnd]) || char.IsWhiteSpace(trimmed[bodyEnd])))
        {
            bodyEnd--;
        }

        if (bodyEnd < 0)
        {
            return new UtteranceCompletenessResult(IsIncomplete: false, Reason: "punctuation_only");
        }

        var body = trimmed[..(bodyEnd + 1)];

        // Complete short acknowledgements ("好的", "是的", "Okay") take precedence
        // over trailing particle heuristics (e.g. "好的" ends with "的").
        if (CompleteShortInterjections.Contains(body))
        {
            return new UtteranceCompletenessResult(IsIncomplete: false, Reason: "complete_interjection");
        }

        // Even when ASR appends a synthetic period at a pause boundary, a dangling
        // conjunction/preposition/article right before the period is a strong signal
        // that the speaker paused mid-clause.
        if (EndsWithDanglingEnglishWord(body))
        {
            return new UtteranceCompletenessResult(IsIncomplete: true, Reason: "dangling_function_word");
        }

        if (EndsWithDanglingCjkConnector(body))
        {
            return new UtteranceCompletenessResult(IsIncomplete: true, Reason: "dangling_cjk_connector");
        }

        if (hasTerminalPunctuation)
        {
            return new UtteranceCompletenessResult(IsIncomplete: false, Reason: "terminal_punctuation");
        }

        return new UtteranceCompletenessResult(IsIncomplete: true, Reason: "missing_terminal_punctuation");
    }

    public static int RecommendedContinuationWindowMs(
        UtteranceCompletenessResult analysis,
        int baseQuickResumeCeilingMs)
    {
        return analysis.IsIncomplete
            ? Math.Max(baseQuickResumeCeilingMs, IncompleteContinuationWindowMs)
            : baseQuickResumeCeilingMs;
    }

    private static bool EndsWithDanglingEnglishWord(string body)
    {
        if (body.Length == 0 || !IsAsciiWordChar(body[^1]))
        {
            return false;
        }

        var start = body.Length - 1;
        while (start > 0 && IsAsciiWordChar(body[start - 1]))
        {
            start--;
        }

        var lastWord = body[start..];
        return DanglingEnglishWords.Contains(lastWord);
    }

    private static bool EndsWithDanglingCjkConnector(string body)
    {
        if (body.Length == 0 || !IsCjkChar(body[^1]))
        {
            return false;
        }

        foreach (var suffix in DanglingCjkSuffixes)
        {
            if (!body.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // Guard against 1-char standalone words like "在" or "的" inside a 1-char interjection,
            // and avoid triggering when the entire utterance is just a single character.
            if (suffix.Length == 1 && body.Length <= 1)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsAsciiWordChar(char ch)
    {
        return ch <= 0x7f && (char.IsLetter(ch) || ch == '\'');
    }

    internal static bool IsCjkChar(char ch)
    {
        return (ch >= '\u4E00' && ch <= '\u9FFF') ||
               (ch >= '\u3400' && ch <= '\u4DBF') ||
               (ch >= '\u3040' && ch <= '\u30FF') ||
               (ch >= '\uAC00' && ch <= '\uD7AF');
    }
}

public readonly record struct UtteranceCompletenessResult(bool IsIncomplete, string Reason);
