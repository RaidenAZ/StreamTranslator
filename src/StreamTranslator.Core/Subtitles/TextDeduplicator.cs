namespace StreamTranslator.Core.Subtitles;

public static class TextDeduplicator
{
    private const int MinChineseOverlap = 3;
    private const int MinLatinOverlapWords = 2;
    private const int MinLatinSingleWordLength = 10;
    private const int MaxAnchorNewOffset = 8;
    private const int MaxPrevTailTokens = 36;
    private const int MaxNewHeadTokens = 48;

    public static TextMergeResult MergeOverlap(string previousText, string newText)
    {
        ArgumentNullException.ThrowIfNull(previousText);
        ArgumentNullException.ThrowIfNull(newText);

        var overlapLength = FindLongestOverlap(previousText, newText);
        if (overlapLength >= MinChineseOverlap)
        {
            var appendedForMerge = newText[overlapLength..];
            return new TextMergeResult(
                previousText + appendedForMerge,
                appendedForMerge.TrimStart(),
                true,
                overlapLength);
        }

        // Fallback: normalized boundary alignment tolerant of punctuation and casing shifts
        // (e.g., ASR appended a closing period on the previous cut and capitalized the overlap).
        var normalized = TryNormalizedSuffixPrefixMerge(previousText, newText);
        if (normalized is not null)
        {
            return normalized;
        }

        return new TextMergeResult(newText, newText, false, 0);
    }

    /// <summary>
    /// Strips transcribed audio left-context (Layer B2) from <paramref name="recognizedWithContext"/>
    /// by finding a normalized token anchor against the tail of <paramref name="previousText"/>.
    /// Tolerates minor casing/punctuation differences, a 1-token ASR variance inside the overlap,
    /// and truncated boundary words (e.g. "The free start." -> "The free starter pack").
    /// </summary>
    public static LeftContextStripResult StripLeftContext(string previousText, string recognizedWithContext)
    {
        ArgumentNullException.ThrowIfNull(previousText);
        ArgumentNullException.ThrowIfNull(recognizedWithContext);

        if (string.IsNullOrWhiteSpace(previousText) || string.IsNullOrWhiteSpace(recognizedWithContext))
        {
            return new LeftContextStripResult(recognizedWithContext, Matched: false, IsPureDuplicate: false, 0, 0);
        }

        var allPrevTokens = Tokenize(previousText);
        var allNewTokens = Tokenize(recognizedWithContext);
        if (allPrevTokens.Count == 0 || allNewTokens.Count == 0)
        {
            return new LeftContextStripResult(recognizedWithContext, Matched: false, IsPureDuplicate: false, 0, 0);
        }

        var prevStart = Math.Max(0, allPrevTokens.Count - MaxPrevTailTokens);
        var prevTokens = allPrevTokens.GetRange(prevStart, allPrevTokens.Count - prevStart);
        var newCount = Math.Min(allNewTokens.Count, MaxNewHeadTokens);
        var newTokens = allNewTokens.GetRange(0, newCount);

        var maxStartNew = Math.Min(MaxAnchorNewOffset, newTokens.Count - 1);
        AnchorMatch? best = null;

        for (var ni = 0; ni <= maxStartNew; ni++)
        {
            for (var pi = 0; pi < prevTokens.Count; pi++)
            {
                if (!string.Equals(prevTokens[pi].Normalized, newTokens[ni].Normalized, StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = EvaluateAnchor(prevTokens, newTokens, pi, ni);
                if (candidate is null)
                {
                    continue;
                }

                if (best is null || candidate.Value.Score > best.Value.Score)
                {
                    best = candidate;
                }
            }
        }

        if (best is null)
        {
            var fallback = MergeOverlap(previousText, recognizedWithContext);
            if (fallback.Deduplicated)
            {
                var pureDup = string.IsNullOrWhiteSpace(fallback.AppendedText);
                return new LeftContextStripResult(
                    fallback.AppendedText,
                    Matched: true,
                    IsPureDuplicate: pureDup,
                    MatchedTokenCount: fallback.OverlapLength,
                    SuggestedPreviousTailTrimLength: 0);
            }

            return new LeftContextStripResult(recognizedWithContext, Matched: false, IsPureDuplicate: false, 0, 0);
        }

        var match = best.Value;
        int cutPos;
        var suggestedTailTrim = 0;

        if (match.TruncatedTailRepair)
        {
            // Keep the full repaired word from recognizedWithContext and tell callers how many
            // trailing chars of previousText were the truncated stub (e.g. "start.").
            cutPos = newTokens[match.RepairNewTokenIndex].RawStart;
            suggestedTailTrim = previousText.Length - prevTokens[match.RepairPrevTokenIndex].RawStart;
        }
        else if (match.RemainingPrevTokens == 0)
        {
            cutPos = AdvancePastLeadingSeparators(recognizedWithContext, newTokens[match.LastMatchedNewIndex].RawEnd);
        }
        else
        {
            // Previous segment had 1-2 garbled tail tokens after a strong >=4-token anchor.
            cutPos = AdvancePastLeadingSeparators(recognizedWithContext, newTokens[match.LastMatchedNewIndex].RawEnd);
            suggestedTailTrim = previousText.Length - prevTokens[match.LastMatchedPrevIndex + 1].RawStart;
        }

        var stripped = cutPos >= recognizedWithContext.Length
            ? ""
            : recognizedWithContext[cutPos..].TrimStart();
        var isPureDuplicate = string.IsNullOrWhiteSpace(stripped);

        return new LeftContextStripResult(
            stripped,
            Matched: true,
            IsPureDuplicate: isPureDuplicate,
            MatchedTokenCount: match.MatchedTokens,
            SuggestedPreviousTailTrimLength: Math.Max(0, suggestedTailTrim));
    }

    private static AnchorMatch? EvaluateAnchor(
        List<AlignedToken> prevTokens,
        List<AlignedToken> newTokens,
        int startPrev,
        int startNew)
    {
        var p = startPrev;
        var n = startNew;
        var matched = 0;
        var matchedCjk = 0;
        var matchedChars = 0;
        var mismatches = 0;
        var lastMatchedP = startPrev;
        var lastMatchedN = startNew;
        var truncatedTailRepair = false;
        var repairNewIndex = -1;
        var repairPrevIndex = -1;

        while (p < prevTokens.Count && n < newTokens.Count)
        {
            if (string.Equals(prevTokens[p].Normalized, newTokens[n].Normalized, StringComparison.Ordinal))
            {
                matched++;
                if (prevTokens[p].IsCjk)
                {
                    matchedCjk++;
                }

                matchedChars += prevTokens[p].Normalized.Length;
                lastMatchedP = p;
                lastMatchedN = n;
                p++;
                n++;
                continue;
            }

            // Detect truncated boundary word at the final token of previousText
            // (e.g. prev ends with "start", new has "starter").
            if (p == prevTokens.Count - 1 &&
                !prevTokens[p].IsCjk &&
                prevTokens[p].Normalized.Length >= 3 &&
                newTokens[n].Normalized.Length > prevTokens[p].Normalized.Length &&
                newTokens[n].Normalized.StartsWith(prevTokens[p].Normalized, StringComparison.Ordinal))
            {
                truncatedTailRepair = true;
                repairPrevIndex = p;
                repairNewIndex = n;
                break;
            }

            // Allow at most one single-token substitution when immediately re-anchored
            // by matching subsequent tokens.
            if (mismatches == 0 &&
                p + 1 < prevTokens.Count &&
                n + 1 < newTokens.Count &&
                string.Equals(prevTokens[p + 1].Normalized, newTokens[n + 1].Normalized, StringComparison.Ordinal))
            {
                mismatches++;
                p++;
                n++;
                continue;
            }

            break;
        }

        var remainingPrev = (prevTokens.Count - 1) - lastMatchedP;
        if (truncatedTailRepair)
        {
            if (remainingPrev > 1)
            {
                return null;
            }
        }
        else if (remainingPrev > 2)
        {
            return null;
        }
        else if (remainingPrev > 0 && matched < 4)
        {
            // Only trim garbled tail tokens from previousText when backed by a strong prefix anchor.
            return null;
        }

        if (!MeetsMatchThreshold(matched, matchedCjk, matchedChars))
        {
            return null;
        }

        var score = matched * 10
                    - mismatches * 4
                    - remainingPrev * 6
                    - startNew * 2
                    + (truncatedTailRepair ? 6 : 0);

        return new AnchorMatch(
            MatchedTokens: matched,
            LastMatchedPrevIndex: lastMatchedP,
            LastMatchedNewIndex: lastMatchedN,
            RemainingPrevTokens: remainingPrev,
            TruncatedTailRepair: truncatedTailRepair,
            RepairPrevTokenIndex: repairPrevIndex,
            RepairNewTokenIndex: repairNewIndex,
            Score: score);
    }

    private static TextMergeResult? TryNormalizedSuffixPrefixMerge(string previousText, string newText)
    {
        if (string.IsNullOrWhiteSpace(previousText) || string.IsNullOrWhiteSpace(newText))
        {
            return null;
        }

        var allPrev = Tokenize(previousText);
        var allNew = Tokenize(newText);
        if (allPrev.Count == 0 || allNew.Count == 0)
        {
            return null;
        }

        var maxK = Math.Min(Math.Min(allPrev.Count, allNew.Count), 24);
        for (var k = maxK; k >= 1; k--)
        {
            var matched = true;
            var cjkCount = 0;
            var charCount = 0;
            for (var i = 0; i < k; i++)
            {
                var pt = allPrev[allPrev.Count - k + i];
                var nt = allNew[i];
                if (!string.Equals(pt.Normalized, nt.Normalized, StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }

                if (pt.IsCjk)
                {
                    cjkCount++;
                }

                charCount += pt.Normalized.Length;
            }

            if (!matched || !MeetsMatchThreshold(k, cjkCount, charCount))
            {
                continue;
            }

            var cutPos = AdvancePastLeadingSeparators(newText, allNew[k - 1].RawEnd);
            var appended = cutPos >= newText.Length ? "" : newText[cutPos..].TrimStart();
            var left = TrimTrailingArtifactPeriod(previousText, appended);
            var merged = appended.Length == 0
                ? left
                : left + (NeedsAsciiSpace(left, appended) ? " " : "") + appended;

            return new TextMergeResult(merged, appended, true, charCount);
        }

        return null;
    }

    private static bool MeetsMatchThreshold(int matchedTokens, int matchedCjkTokens, int matchedChars)
    {
        if (matchedCjkTokens > 0)
        {
            return matchedTokens >= MinChineseOverlap;
        }

        return (matchedTokens >= MinLatinOverlapWords && matchedChars >= 6) ||
               (matchedTokens == 1 && matchedChars >= MinLatinSingleWordLength);
    }

    private static int AdvancePastLeadingSeparators(string text, int pos)
    {
        var cursor = pos;
        while (cursor < text.Length)
        {
            var ch = text[cursor];
            if (char.IsWhiteSpace(ch) || ch is ',' or '，' or '、' or '.' or '。' or ';' or '；' or ':' or '：')
            {
                cursor++;
                continue;
            }

            break;
        }

        return cursor;
    }

    private static string TrimTrailingArtifactPeriod(string previousText, string appended)
    {
        var left = previousText.TrimEnd();
        if (left.Length > 0 && appended.Length > 0 && left[^1] is '.' or '。')
        {
            if (char.IsLower(appended[0]) || UtteranceCompletenessAnalyzer.IsCjkChar(appended[0]))
            {
                return left[..^1];
            }
        }

        return left;
    }

    private static bool NeedsAsciiSpace(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        var l = left[^1];
        var r = right[0];
        return l <= 0x7f && r <= 0x7f && !char.IsWhiteSpace(l) && !char.IsWhiteSpace(r);
    }

    private static List<AlignedToken> Tokenize(string text)
    {
        var tokens = new List<AlignedToken>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (UtteranceCompletenessAnalyzer.IsCjkChar(ch))
            {
                tokens.Add(new AlignedToken(ch.ToString(), i, i + 1, IsCjk: true));
                i++;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                var start = i;
                while (i < text.Length &&
                       (char.IsLetterOrDigit(text[i]) ||
                        (text[i] == '\'' && i + 1 < text.Length && char.IsLetter(text[i + 1]))) &&
                       !UtteranceCompletenessAnalyzer.IsCjkChar(text[i]))
                {
                    i++;
                }

                var normalized = text[start..i].ToLowerInvariant();
                tokens.Add(new AlignedToken(normalized, start, i, IsCjk: false));
                continue;
            }

            i++;
        }

        return tokens;
    }

    private static int FindLongestOverlap(string previousText, string newText)
    {
        var maxLength = Math.Min(previousText.Length, newText.Length);
        for (var length = maxLength; length >= 1; length--)
        {
            if (previousText.AsSpan(previousText.Length - length, length).SequenceEqual(newText.AsSpan(0, length)))
            {
                return length;
            }
        }

        return 0;
    }

    private readonly record struct AlignedToken(string Normalized, int RawStart, int RawEnd, bool IsCjk);

    private readonly record struct AnchorMatch(
        int MatchedTokens,
        int LastMatchedPrevIndex,
        int LastMatchedNewIndex,
        int RemainingPrevTokens,
        bool TruncatedTailRepair,
        int RepairPrevTokenIndex,
        int RepairNewTokenIndex,
        int Score);
}

public sealed record TextMergeResult(
    string MergedText,
    string AppendedText,
    bool Deduplicated,
    int OverlapLength);

public sealed record LeftContextStripResult(
    string StrippedText,
    bool Matched,
    bool IsPureDuplicate,
    int MatchedTokenCount,
    int SuggestedPreviousTailTrimLength);
