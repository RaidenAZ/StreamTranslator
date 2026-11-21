namespace StreamTranslator.Core.Subtitles;

/// <summary>
/// Scans accumulated ASR text for sentence boundaries using multilingual punctuation heuristics.
/// Supports both English terminal punctuation ([.!?] + whitespace + capital) and CJK terminal
/// punctuation ([。！？]). Returns the character index at which the next sentence begins, or -1.
/// </summary>
public static class SentenceBoundaryScanner
{
    /// <summary>
    /// Minimum word-equivalent units a sentence must have to be considered a valid flush boundary.
    /// Short fragments like "The free start." are excluded by this guard.
    /// </summary>
    public const int MinSentenceWords = 5;

    /// <summary>
    /// Finds the last valid sentence boundary in <paramref name="text"/> and returns
    /// the index of the first character of the following sentence.
    /// Returns -1 if no valid boundary was found.
    /// </summary>
    public static int FindLastBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }

        var lastBoundary = -1;

        for (var i = 0; i < text.Length - 1; i++)
        {
            var ch = text[i];

            if (ch is '。' or '！' or '？')
            {
                var nextStart = i + 1;
                while (nextStart < text.Length && IsCjkBoundaryTrailingWrapper(text[nextStart]))
                {
                    nextStart++;
                }

                if (nextStart >= text.Length)
                {
                    continue;
                }

                if (CountWords(text, 0, i + 1) < MinSentenceWords)
                {
                    continue;
                }

                lastBoundary = nextStart;
                continue;
            }

            if (ch is not ('.' or '!' or '?'))
            {
                continue;
            }

            // Must be followed by whitespace.
            var afterPunct = i + 1;
            if (!char.IsWhiteSpace(text[afterPunct]))
            {
                continue;
            }

            // The word ending with this punctuation must be > 2 chars to exclude
            // abbreviations such as "Mr." or "St." (single/double-char stems).
            var wordStart = WordStartBefore(text, i);
            if (i - wordStart <= 2)
            {
                continue;
            }

            // Skip whitespace to find the start of the next token.
            var nextWordStart = afterPunct + 1;
            while (nextWordStart < text.Length && char.IsWhiteSpace(text[nextWordStart]))
            {
                nextWordStart++;
            }

            // Next token must start with an uppercase letter (or CJK character in code-switched speech).
            if (nextWordStart >= text.Length ||
                (!char.IsUpper(text[nextWordStart]) && !UtteranceCompletenessAnalyzer.IsCjkChar(text[nextWordStart])))
            {
                continue;
            }

            // The sentence ending here must be long enough (guards against truncated
            // fragments that the ASR model punctuated prematurely at a hardMax cut).
            if (CountWords(text, 0, i) < MinSentenceWords)
            {
                continue;
            }

            lastBoundary = nextWordStart;
        }

        return lastBoundary;
    }

    private static bool IsCjkBoundaryTrailingWrapper(char ch)
    {
        return char.IsWhiteSpace(ch) || ch is '”' or '’' or '」' or '』' or '》' or '）' or '"' or '\'';
    }

    private static int WordStartBefore(string text, int position)
    {
        var i = position - 1;
        while (i >= 0 && !char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i + 1;
    }

    /// <summary>
    /// Counts word-equivalent units in [start, end). Space-delimited Latin tokens count
    /// as 1 unit each (preserving exact English behavior), while every 2 CJK ideographs
    /// count as 1 unit so CJK clauses meet the same sentence length thresholds.
    /// </summary>
    internal static int CountWords(string text, int start, int end)
    {
        var latinWords = 0;
        var cjkChars = 0;
        var inLatinWord = false;
        var limit = Math.Min(end, text.Length);

        for (var i = start; i < limit; i++)
        {
            var ch = text[i];
            if (UtteranceCompletenessAnalyzer.IsCjkChar(ch))
            {
                inLatinWord = false;
                cjkChars++;
            }
            else if (char.IsWhiteSpace(ch) || ch is '。' or '，' or '！' or '？' or '、' or '；' or '：')
            {
                inLatinWord = false;
            }
            else if (!inLatinWord)
            {
                inLatinWord = true;
                latinWords++;
            }
        }

        return latinWords + (cjkChars / 2);
    }
}
