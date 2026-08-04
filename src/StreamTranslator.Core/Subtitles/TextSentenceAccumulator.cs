namespace StreamTranslator.Core.Subtitles;

/// <summary>
/// Buffers ASR output across hardMax-cut boundaries and emits semantically complete
/// sentence units for translation and history recording.
///
/// Flush rules (priority order):
///   1. CutReason != HardMax  →  immediate flush (natural VAD boundary or revision).
///   2. Sentence boundary found in accumulated text  →  flush up to the boundary.
///   3. Combined text length exceeds ForceFlushThreshold  →  force flush.
///
/// Thread-safety: all public methods must be called from the same thread (UI / dispatch).
/// </summary>
public sealed class TextSentenceAccumulator
{
    private const int PreviousSourceTailLength = 120;

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

        _buffer.Add(item);

        // Revision items bypass accumulation and are emitted directly so the
        // history revision mechanism stays immediate and unaffected.
        var isRevision = string.Equals(item.Type, "subtitle_revision", StringComparison.Ordinal);
        if (isRevision)
        {
            _buffer.RemoveAt(_buffer.Count - 1);
            SentenceUnitReady?.Invoke(item);   // pass through as-is, no Type override
            return;
        }

        // Non-hardMax cuts (Silence, SoftMax): flush immediately.
        var isHardMax = string.Equals(item.CutReason, "HardMax", StringComparison.Ordinal);
        if (!isHardMax)
        {
            FlushAll();
            return;
        }

        var combined = BuildCombinedText();

        // Check for a sentence boundary in the accumulated text.
        var boundary = SentenceBoundaryScanner.FindLastBoundary(combined);
        if (boundary > 0)
        {
            var sentenceText = combined[..boundary].TrimEnd();
            var remainderText = combined[boundary..].TrimStart();
            FlushUpTo(sentenceText);
            RebuildBufferFromRemainder(remainderText);
            return;
        }

        // Force flush when the buffer has grown too large.
        if (combined.Length >= ForceFlushThreshold)
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

    private string BuildCombinedText()
    {
        return string.Join(" ", _buffer.Select(item => item.SourceText.Trim()));
    }

    private void FlushAll()
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        var combinedText = BuildCombinedText();
        EmitUnit(combinedText, _buffer[0], _buffer[^1]);
        _buffer.Clear();
    }

    private void FlushUpTo(string sentenceText)
    {
        // Determine the last buffer item whose text is fully covered by sentenceText.
        // We attribute the sentence to all buffer items up to (and including) the one
        // that tips the combined length past sentenceText.Length.
        var accumulated = 0;
        var lastConsumedIndex = _buffer.Count - 1;
        for (var i = 0; i < _buffer.Count; i++)
        {
            var contribution = (i == 0 ? 0 : 1) + _buffer[i].SourceText.Trim().Length;
            accumulated += contribution;
            if (accumulated >= sentenceText.Length)
            {
                lastConsumedIndex = i;
                break;
            }
        }

        var first = _buffer[0];
        var last = _buffer[lastConsumedIndex];
        EmitUnit(sentenceText, first, last);

        // Remove consumed items.
        _buffer.RemoveRange(0, lastConsumedIndex + 1);
    }

    private void RebuildBufferFromRemainder(string remainderText)
    {
        if (string.IsNullOrWhiteSpace(remainderText) || _buffer.Count == 0)
        {
            return;
        }

        // Update the first remaining buffer item's SourceText to the remainder so
        // the next cycle starts with clean text that matches what was accumulated.
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
            TranslatedText = null
        };

        SentenceUnitReady?.Invoke(unit);
    }
}
