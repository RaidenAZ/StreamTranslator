using Microsoft.VisualStudio.TestTools.UnitTesting;
using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SentenceBoundaryScannerTests
{
    // ── No boundary ──────────────────────────────────────────────────────────

    [TestMethod]
    public void NoBoundary_EmptyText_ReturnsMinusOne()
        => Assert.AreEqual(-1, SentenceBoundaryScanner.FindLastBoundary(""));

    [TestMethod]
    public void NoBoundary_WhitespaceOnly_ReturnsMinusOne()
        => Assert.AreEqual(-1, SentenceBoundaryScanner.FindLastBoundary("   "));

    [TestMethod]
    public void NoBoundary_SingleWord_ReturnsMinusOne()
        => Assert.AreEqual(-1, SentenceBoundaryScanner.FindLastBoundary("Hello"));

    [TestMethod]
    public void NoBoundary_PeriodAtEnd_NoNextCapital_ReturnsMinusOne()
        => Assert.AreEqual(-1, SentenceBoundaryScanner.FindLastBoundary("Hello world."));

    [TestMethod]
    public void NoBoundary_TooFewWords_ReturnsMinusOne()
    {
        // Sentence "Hi there." has only 2 words — below MinSentenceWords (5)
        var text = "Hi there. Next sentence starts here.";
        Assert.AreEqual(-1, SentenceBoundaryScanner.FindLastBoundary(text));
    }

    [TestMethod]
    public void NoBoundary_Abbreviation_Mr_ReturnsMinusOne()
    {
        // "Mr." is only 2 chars before the dot — filtered as abbreviation
        var text = "Please call Mr. Smith for the details next time.";
        Assert.AreEqual(-1, SentenceBoundaryScanner.FindLastBoundary(text));
    }

    [TestMethod]
    public void NoBoundary_Abbreviation_St_ReturnsMinusOne()
    {
        var text = "She lives on St. Georges Avenue and walks every day.";
        Assert.AreEqual(-1, SentenceBoundaryScanner.FindLastBoundary(text));
    }

    // ── Valid boundary found ─────────────────────────────────────────────────

    [TestMethod]
    public void FindsBoundary_TwoSentences_ReturnsStartOfSecond()
    {
        var text = "Train Sim World Six will be coming on September 2025. Five, coming with a top-notch selection of routes.";
        var idx = SentenceBoundaryScanner.FindLastBoundary(text);
        Assert.IsTrue(idx > 0, "Should find a boundary");
        Assert.AreEqual("Five,", text[idx..].Split(' ')[0]);
    }

    [TestMethod]
    public void FindsBoundary_QuestionMark_ReturnsStartOfNextSentence()
    {
        var text = "Are you ready for the next big feature announcement? Train Sim World Six is coming very soon.";
        var idx = SentenceBoundaryScanner.FindLastBoundary(text);
        Assert.IsTrue(idx > 0);
        Assert.IsTrue(text[idx..].StartsWith("Train", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FindsBoundary_ExclamationMark_ReturnsStartOfNextSentence()
    {
        var text = "This is the most exciting announcement of the year! We have a lot more to share with everyone.";
        var idx = SentenceBoundaryScanner.FindLastBoundary(text);
        Assert.IsTrue(idx > 0);
        Assert.IsTrue(text[idx..].StartsWith("We", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FindsBoundary_MultipleSentences_ReturnsLast()
    {
        var text = "The first sentence ends here today. Then the second sentence follows on nicely. Finally the third one arrives.";
        var idx = SentenceBoundaryScanner.FindLastBoundary(text);
        Assert.IsTrue(idx > 0);
        Assert.IsTrue(text[idx..].StartsWith("Finally", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FindsBoundary_RealLogSentence_CorrectSplit()
    {
        // From the actual test session data
        const string text =
            "Oh, I am excited, Matt. We have a lot to talk about. We do. First, we want to reveal the next edition in the series. Train Sim World Six will be coming on September 2025.";
        var idx = SentenceBoundaryScanner.FindLastBoundary(text);
        // The last valid boundary should be before "Train Sim World"
        // (The "We do." sentence is only 2 words and gets filtered)
        Assert.IsTrue(idx > 0, "Should find a boundary in real log data");
        // The returned index should point to a capital letter
        Assert.IsTrue(char.IsUpper(text[idx]), "Boundary should point to uppercase letter");
    }

    [TestMethod]
    public void FindsBoundary_PeriodNotFollowedBySpace_Ignored()
    {
        // e.g. "9.30" or "V1.2" — digit/lowercase after period, not a sentence end
        var text = "The version is V1.2 and ships on September 2025. It comes with many new features here.";
        var idx = SentenceBoundaryScanner.FindLastBoundary(text);
        Assert.IsTrue(idx > 0);
        // Should find the boundary at "It comes…"
        Assert.IsTrue(text[idx..].StartsWith("It", StringComparison.Ordinal));
    }
}
