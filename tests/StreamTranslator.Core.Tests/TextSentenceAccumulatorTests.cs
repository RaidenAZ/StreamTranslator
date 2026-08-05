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

    // ── HardMax seam boundary validation (Direction C) ───────────────────────

    [TestMethod]
    public void SeamBoundary_ShortSentenceAtSeam_BlocksFalseFlush()
    {
        // 9-word sentence ending right at a HardMax seam must not trigger flush.
        // Guards against ASR artifact periods where the model "closes" a mid-sentence
        // cut before a capital word in the next segment.
        // 9 words: We(1) will(2) take(3) a(4) look(5) at(6) the(7) new(8) roadmap(9).
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("We will take a look at the new roadmap.", "HardMax"));
        acc.Add(MakeItem("Players can expect to see new features.", "HardMax"));

        Assert.AreEqual(0, emitted.Count,
            "Seam boundary with fewer than MinSentenceWordsAtHardMaxSeam words must be blocked");
    }

    [TestMethod]
    public void SeamBoundary_LongSentenceAtSeam_Flushes()
    {
        // A 10-word sentence (== MinSentenceWordsAtHardMaxSeam) at the seam must flush.
        // Train(1) Sim(2) World(3) Six(4) will(5) be(6) coming(7) on(8) September(9) 2025(10).
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        acc.Add(MakeItem("Train Sim World Six will be coming on September 2025.", "HardMax"));
        acc.Add(MakeItem("Five, coming with top-notch routes.", "HardMax"));

        Assert.AreEqual(1, emitted.Count, "10-word sentence at seam must flush (≥ threshold)");
        StringAssert.Contains(emitted[0].SourceText, "2025");
    }

    [TestMethod]
    public void SeamBoundary_InternalBoundary_NotAffectedBySeamCheck()
    {
        // When the seam boundary is blocked by the word-count guard, a real boundary
        // found inside the second segment must still cause a flush.
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        // Short sentence at seam (9 words → blocked)
        acc.Add(MakeItem("We will take a look at the new roadmap.", "HardMax"));
        // Second item has a real internal boundary "worry. So"
        acc.Add(MakeItem(
            "Players expect more. Don't you worry. So I think we should get this party started now.",
            "HardMax"));

        Assert.AreEqual(1, emitted.Count,
            "Internal boundary inside second item must flush even when seam boundary is blocked");
        StringAssert.Contains(emitted[0].SourceText, "worry");
    }

    [TestMethod]
    public void SeamBoundary_BoundaryInsideFirstItem_NotASeam_Flushes()
    {
        // A boundary found within the first item's own text is not at any seam and
        // must flush normally without the seam word-count gate applying.
        var emitted = new List<SubtitleItem>();
        var acc = new TextSentenceAccumulator(350);
        acc.SentenceUnitReady += unit => emitted.Add(unit);

        // First item has an internal boundary; second item is lowercase continuation
        acc.Add(MakeItem(
            "We will announce the full schedule in October. Then we will discuss the details.",
            "HardMax"));
        acc.Add(MakeItem("right after the main presentation ends.", "HardMax"));

        // "October. Then" is inside item1 — not at any HardMax seam → no word-count gate
        Assert.AreEqual(1, emitted.Count, "Boundary inside an item's own text must flush");
        StringAssert.Contains(emitted[0].SourceText, "October");
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
