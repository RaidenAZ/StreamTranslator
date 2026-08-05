namespace StreamTranslator.Core.Subtitles;

/// <summary>
/// Scans accumulated ASR text for sentence boundaries using punctuation heuristics.
/// Designed for English conversational/presentation speech where the ASR model outputs
/// properly punctuated text. Returns the character index at which the next sentence
/// begins (i.e. just past the whitespace following terminal punctuation), or -1.
/// </summary>
public static class SentenceBoundaryScanner
{
    /// <summary>
    /// Minimum word count a sentence must have to be considered a valid flush boundary.
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

            // Next token must start with an uppercase letter.
            if (nextWordStart >= text.Length || !char.IsUpper(text[nextWordStart]))
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

    private static int WordStartBefore(string text, int position)
    {
        var i = position - 1;
        while (i >= 0 && !char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i + 1;
    }

    internal static int CountWords(string text, int start, int end)
    {
        var count = 0;
        var inWord = false;
        for (var i = start; i < end && i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }
}
