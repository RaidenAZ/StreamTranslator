using System.Text;

namespace StreamTranslator.Core.Subtitles;

/// <summary>
/// Buffers ASR output across hardMax / incomplete softMax cut boundaries and emits
/// semantically complete sentence units for translation and history recording.
///
/// Flush rules (priority order):
///   1. CutReason is Silence or a semantically complete SoftMax  →  immediate flush.
///   2. Sentence boundary found in accumulated text  →  flush up to the boundary,
///      subject to seam validation (see IsValidBoundaryAtSeam). A boundary that falls
///      exactly at the join point between two forced-cut segments is a candidate for an
///      ASR artifact period; it is only accepted when the sentence before the seam
///      contains ≥ MinSentenceWordsAtHardMaxSeam word-equivalent units.
///   3. Combined text length exceeds ForceFlushThreshold AND more than one item is
///      buffered  →  force flush. A single forced-cut item never force-flushes on its own;
///      it always waits for the next segment so the two can be evaluated together.
///
/// Thread-safety: all public methods must be called from the same thread (UI / dispatch).
/// </summary>
public sealed class TextSentenceAccumulator
{
    private const int PreviousSourceTailLength = 120;

    /// <summary>
    /// Minimum word count required for a sentence boundary that falls exactly at the
    /// seam between two forced-cut buffer items. ASR models commonly append a closing period
    /// when audio is cut mid-sentence; a short sentence at a seam is likely an artifact.
    /// Boundaries with fewer words than this threshold are demoted to "no boundary".
    /// </summary>
    private const int MinSentenceWordsAtHardMaxSeam = 10;

    /// <summary>
    /// Character tolerance when checking whether a boundary position coincides with a
    /// forced-cut seam. Accounts for minor whitespace variation in the combined text.
    /// </summary>
    private const int SeamBoundaryTolerance = 1;

    private readonly List<SubtitleItem> _buffer = [];
    private string _previousSentenceSourceTail = "";

    public TextSentenceAccumulator(int forceFlushThreshold)
    {
        ForceFlushThreshold = Math.Clamp(forceFlushThreshold, 50, 800);
    }

    /// <summary>Maximum combined source text length before a force flush.</summary>
    public int ForceFlushThreshold { get; }

    /// <summary>
    /// Raised when a sentence unit is ready for translation and history recording.
    /// The item has <c>Type = "sentence_unit"</c> and carries
    /// <see cref="SubtitleItem.PreviousSourceTail"/> for translation context.
    /// </summary>
    public event Action<SubtitleItem>? SentenceUnitReady;

    /// <summary>Adds an ASR result item and triggers flush logic.</summary>
    public void Add(SubtitleItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SourceText))
        {
            return;
        }

        // Revision items bypass accumulation and are emitted directly so the
        // history revision mechanism stays immediate and unaffected.
        var isRevision = string.Equals(item.Type, "subtitle_revision", StringComparison.Ordinal);
        if (isRevision)
        {
            SentenceUnitReady?.Invoke(item);   // pass through as-is, no Type override
            return;
        }

        // If Layer B2 left-context alignment identified a truncated boundary stub on the
        // previous buffered item (e.g. "The free start." -> next item begins with "starter pack"),
        // cleanly trim the stub from the buffered item before joining.
        if (_buffer.Count > 0 && item.PreviousTailTrimLength > 0)
        {
            var prev = _buffer[^1];
            var prevText = prev.SourceText;
            if (item.PreviousTailTrimLength < prevText.Length)
            {
                var trimmedPrev = prevText[..^item.PreviousTailTrimLength].TrimEnd();
                if (trimmedPrev.Length > 0)
                {
                    _buffer[^1] = prev with { SourceText = trimmedPrev };
                }
            }
        }

        _buffer.Add(item);

        // Non-hardMax cuts flush immediately unless SoftMax cut an incomplete mid-sentence clause.
        var isHardMax = string.Equals(item.CutReason, "HardMax", StringComparison.Ordinal);
        var isIncompleteSoftMax = string.Equals(item.CutReason, "SoftMax", StringComparison.Ordinal) &&
                                  UtteranceCompletenessAnalyzer.Analyze(item.SourceText).IsIncomplete;
        if (!isHardMax && !isIncompleteSoftMax)
        {
            FlushAll();
            return;
        }

        var combined = BuildCombinedText(out var seamPositions, out var itemEndOffsets);

        // Check for a sentence boundary in the accumulated text.
        var boundary = SentenceBoundaryScanner.FindLastBoundary(combined);

        // Validate seam boundaries: a boundary that lands exactly at the join point
        // between two forced-cut items may be caused by an ASR artifact period. Require
        // a higher word count to accept it.
        if (boundary > 0 && !IsValidBoundaryAtSeam(boundary, combined, seamPositions))
        {
            boundary = -1;
        }

        if (boundary > 0)
        {
            var sentenceText = combined[..boundary].TrimEnd();
            var remainderText = combined[boundary..].TrimStart();
            FlushUpTo(sentenceText, itemEndOffsets);
            RebuildBufferFromRemainder(remainderText);
            return;
        }

        // Force flush when the buffer has grown too large.
        // A single forced-cut item never triggers force-flush on its own — it must wait
        // for at least one more segment so the pair can be evaluated together.
        if (_buffer.Count > 1 && combined.Length >= ForceFlushThreshold)
        {
            FlushAll();
        }
    }

    /// <summary>
    /// Flushes any remaining buffered items, typically called when the session stops.
    /// </summary>
    public void Flush()
    {
        if (_buffer.Count > 0)
        {
            FlushAll();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers

    private string BuildCombinedText(out int[] forcedSeamPositions, out int[] itemEndOffsets)
    {
        var sb = new StringBuilder();
        var seams = new List<int>();
        itemEndOffsets = new int[_buffer.Count];

        for (var i = 0; i < _buffer.Count; i++)
        {
            var part = _buffer[i].SourceText.Trim();
            if (i > 0 && sb.Length > 0 && part.Length > 0 && NeedsSeparator(sb[^1], part[0]))
            {
                sb.Append(' ');
            }

            if (i > 0 && IsForcedCut(_buffer[i - 1].CutReason))
            {
                seams.Add(sb.Length);
            }

            sb.Append(part);
            itemEndOffsets[i] = sb.Length;
        }

        forcedSeamPositions = [.. seams];
        return sb.ToString();
    }

    private static bool IsForcedCut(string? cutReason)
    {
        return string.Equals(cutReason, "HardMax", StringComparison.Ordinal) ||
               string.Equals(cutReason, "SoftMax", StringComparison.Ordinal);
    }

    private static bool NeedsSeparator(char left, char right)
    {
        if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right))
        {
            return false;
        }

        if (UtteranceCompletenessAnalyzer.IsCjkChar(left) ||
            UtteranceCompletenessAnalyzer.IsCjkChar(right) ||
            left is '。' or '，' or '！' or '？' or '、' or '；' or '：' or '”' or '」')
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="boundary"/> is a valid flush point.
    /// A boundary that falls within <see cref="SeamBoundaryTolerance"/> characters of
    /// a forced-cut seam is treated as suspicious and must meet the higher word-count
    /// requirement <see cref="MinSentenceWordsAtHardMaxSeam"/> to be accepted.
    /// </summary>
    private static bool IsValidBoundaryAtSeam(int boundary, string combined, int[] seamPositions)
    {
        var isAtSeam = seamPositions.Any(seam => Math.Abs(seam - boundary) <= SeamBoundaryTolerance);
        if (!isAtSeam)
        {
            return true;
        }

        var sentenceText = combined[..boundary].TrimEnd();
        return SentenceBoundaryScanner.CountWords(sentenceText, 0, sentenceText.Length)
               >= MinSentenceWordsAtHardMaxSeam;
    }

    private void FlushAll()
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        var combinedText = BuildCombinedText(out _, out _);
        EmitUnit(combinedText, _buffer[0], _buffer[^1]);
        _buffer.Clear();
    }

    private void FlushUpTo(string sentenceText, int[] itemEndOffsets)
    {
        var lastConsumedIndex = _buffer.Count - 1;
        for (var i = 0; i < itemEndOffsets.Length; i++)
        {
            if (itemEndOffsets[i] >= sentenceText.Length)
            {
                lastConsumedIndex = i;
                break;
            }
        }

        var first = _buffer[0];
        var last = _buffer[lastConsumedIndex];
        EmitUnit(sentenceText, first, last);

        _buffer.RemoveRange(0, lastConsumedIndex + 1);
    }

    private void RebuildBufferFromRemainder(string remainderText)
    {
        if (string.IsNullOrWhiteSpace(remainderText) || _buffer.Count == 0)
        {
            return;
        }

        _buffer[0] = _buffer[0] with { SourceText = remainderText };
    }

    private void EmitUnit(string sourceText, SubtitleItem first, SubtitleItem last)
    {
        var tail = string.IsNullOrEmpty(_previousSentenceSourceTail) ? null : _previousSentenceSourceTail;
        _previousSentenceSourceTail = sourceText.Length <= PreviousSourceTailLength
            ? sourceText
            : sourceText[^PreviousSourceTailLength..];

        var unit = last with
        {
            Type = "sentence_unit",
            SourceText = sourceText,
            Start = first.Start,
            End = last.End,
            GeneratedAt = last.GeneratedAt,
            CutReason = null,
            PreviousSourceTail = tail,
            TranslatedText = null,
            ReplacesGroupSourceText = false,
            PreviousTailTrimLength = 0
        };

        SentenceUnitReady?.Invoke(unit);
    }
}
