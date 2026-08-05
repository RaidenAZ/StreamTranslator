using Microsoft.VisualStudio.TestTools.UnitTesting;
using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class TextSentenceAccumulatorTests
{
    private static SubtitleItem MakeItem(string text, string cutReason = "HardMax", string type = "subtitle") =>
        new()
        {
            Type = type,
            Sequence = 1,
            UtteranceGroupId = "grp-001",
            Revision = 1,
            ReplacesSequences = [1],
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(10),
            GeneratedAt = DateTimeOffset.UtcNow,
            SourceText = text,
            Status = SubtitleStatus.Final,
            CutReason = cutReason
        };

    // ── Immediate flush on natural boundary ──────────────────────────────────

    [TestMethod]
    public void Flush_OnSilence_ImmediatelyEmits()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Hello world this is a test.", "Silence"));

        Assert.AreEqual(1, emitted.Count);
        Assert.AreEqual("sentence_unit", emitted[0].Type);
    }

    [TestMethod]
    public void Flush_OnSoftMax_ImmediatelyEmits()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Hello world this is a test.", "SoftMax"));

        Assert.AreEqual(1, emitted.Count);
    }

    [TestMethod]
    public void NoFlush_OnHardMax_WithoutBoundaryOrThreshold()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Five coming with", "HardMax")); // no sentence boundary, short

        Assert.AreEqual(0, emitted.Count, "Should not flush without boundary or threshold");
    }

    // ── Sentence boundary flush ───────────────────────────────────────────────

    [TestMethod]
    public void Flush_OnSentenceBoundary_EmitsSentence()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Train Sim World Six will be coming on September 2025."));
        acc.Add(MakeItem("Five, coming with top-notch routes."));

        // First add: no boundary yet (single sentence, would need next word capital)
        // Second add: combined text now has a boundary after "2025."
        Assert.AreEqual(1, emitted.Count, "Should emit one sentence unit");
        StringAssert.Contains(emitted[0].SourceText, "2025");
    }

    [TestMethod]
    public void Flush_OnSentenceBoundary_RemainderStartsNewCycle()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Train Sim World Six will be coming on September 2025."));
        acc.Add(MakeItem("Five, coming with top-notch routes and trains."));
        // Now add a silence that flushes the remainder
        acc.Add(MakeItem("Thank you.", "Silence"));

        Assert.AreEqual(2, emitted.Count, "Silence should flush the remaining buffer");
    }

    // ── Force-flush ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Flush_OnThresholdExceeded_ForceFlushes()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(50); // low threshold for testing

        acc.SentenceUnitReady += unit => emitted.Add(unit);

        // Two HardMax items whose combined text exceeds threshold → force-flush.
        acc.Add(MakeItem("no boundary here x", "HardMax"));
        acc.Add(MakeItem("and still no boundary to be found anywhere", "HardMax"));

        Assert.AreEqual(1, emitted.Count, "Should force-flush when combined text exceeds threshold");
        Assert.AreEqual("sentence_unit", emitted[0].Type);
    }

    [TestMethod]
    public void SingleHardMax_ExceedingThreshold_ShouldNotForceFlush()
    {
        // A single HardMax segment must never force-flush on its own, regardless of length.
        // It must wait for the next segment so the two can be evaluated together,
        // preventing a long HardMax segment from bypassing accumulation entirely.
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(50); // very low threshold

        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem(
            "This is a very long hardmax segment with many words and absolutely no sentence boundary anywhere in the text.",
            "HardMax"));

        Assert.AreEqual(0, emitted.Count,
            "Single HardMax segment must not force-flush regardless of how long it is");
    }

    [TestMethod]
    public void TwoHardMax_SecondPushesOverThreshold_ForceFlushes()
    {
        // Confirm force-flush still fires once a second segment tips the buffer over.
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(50);

        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("First long segment no boundary", "HardMax"));
        acc.Add(MakeItem("second segment pushes combined over fifty chars total", "HardMax"));

        Assert.AreEqual(1, emitted.Count, "Force-flush should fire when two items exceed threshold");
    }

    // ── Trailing-period stripping (P2) ───────────────────────────────────────

    [TestMethod]
    public void CombinedText_StripsTrailingPeriodFromHardMaxItemFollowedByMore()
    {
        // When a HardMax item ends with a short-stem period (≤ 2-char word, e.g. "6.")
        // and is followed by more content, that artifact period must be stripped so
        // the joined translation input does not carry a spurious mid-sentence period.
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        // Matches the real log case: ASR closes "...World 6" with an artifact period
        acc.Add(MakeItem("We will be taking a look at the Train Sim World 6.", "HardMax"));
        // Continuation arrives via Silence, triggering flush
        acc.Add(MakeItem("Roadmap and more add-ons are coming soon.", "Silence"));

        Assert.AreEqual(1, emitted.Count);
        // Artifact "6. Roadmap" pattern must be gone — stripped to "6 Roadmap"
        StringAssert.DoesNotMatch(
            emitted[0].SourceText,
            new System.Text.RegularExpressions.Regex(@"\b6\.\s+Roadmap"));
        StringAssert.Contains(emitted[0].SourceText, "6 Roadmap");
    }

    [TestMethod]
    public void CombinedText_PreservesPeriodOnSilenceItem()
    {
        // The trailing period on a Silence-terminated item is a real sentence end
        // and must not be stripped; only HardMax items followed by more content lose theirs.
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("We will take a look at the roadmap.", "Silence"));

        Assert.AreEqual(1, emitted.Count);
        StringAssert.Contains(emitted[0].SourceText, "roadmap.");
    }

    [TestMethod]
    public void CombinedText_OnlyStripsHardMax_NotIntermediateHardMaxAtEnd()
    {
        // A HardMax item that is the last in the buffer (no subsequent item yet)
        // must keep its period, because we don't know whether more content is coming.
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        // Single HardMax item, stays in buffer — manually flush to emit it
        acc.Add(MakeItem("The segment ends here.", "HardMax"));
        acc.Flush();

        Assert.AreEqual(1, emitted.Count);
        // Period preserved because it was the only / last item
        StringAssert.Contains(emitted[0].SourceText, "here.");
    }

    // ── PreviousSourceTail ────────────────────────────────────────────────────

    [TestMethod]
    public void PreviousSourceTail_SecondFlush_CarriesFirstSentenceTail()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Train Sim World Six will be coming on September 2025.", "Silence"));
        acc.Add(MakeItem("Five, coming with top-notch routes.", "Silence"));

        Assert.AreEqual(2, emitted.Count);
        Assert.IsNull(emitted[0].PreviousSourceTail, "First unit has no previous tail");
        Assert.IsNotNull(emitted[1].PreviousSourceTail, "Second unit should carry first unit's tail");
    }

    // ── Revision bypass ───────────────────────────────────────────────────────

    [TestMethod]
    public void Revision_BypassesAccumulator_EmitsImmediately()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Some hardmax text here", "HardMax"));  // buffered
        acc.Add(MakeItem("Corrected text here.", "HardMax", type: "subtitle_revision"));

        Assert.AreEqual(1, emitted.Count, "Revision should bypass accumulator");
        Assert.AreEqual("subtitle_revision", emitted[0].Type);
    }

    // ── Manual Flush ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Flush_ManualFlush_EmitsBufferedItems()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Buffered text no boundary yet okay.", "HardMax"));
        Assert.AreEqual(0, emitted.Count);

        acc.Flush();
        Assert.AreEqual(1, emitted.Count, "Manual flush should emit buffered items");
    }

    [TestMethod]
    public void Flush_ManualFlush_EmptyBuffer_DoesNotEmit()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Flush();
        Assert.AreEqual(0, emitted.Count);
    }

    // ── SentenceUnit fields ───────────────────────────────────────────────────

    [TestMethod]
    public void SentenceUnit_SpansStartAndEndOfContributingItems()
    {
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        var item1 = MakeItem("First contributing segment text here.", "HardMax") with
        {
            Start = TimeSpan.FromSeconds(5),
            End = TimeSpan.FromSeconds(15)
        };
        var item2 = MakeItem("Second contributing segment text here.", "Silence") with
        {
            Start = TimeSpan.FromSeconds(15),
            End = TimeSpan.FromSeconds(25)
        };
        acc.Add(item1);
        acc.Add(item2);

        Assert.AreEqual(1, emitted.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(5), emitted[0].Start, "Start should be earliest");
        Assert.AreEqual(TimeSpan.FromSeconds(25), emitted[0].End, "End should be latest");
    }
}
