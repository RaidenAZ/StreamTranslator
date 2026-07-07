namespace StreamTranslator.Core.Subtitles;

public static class TextDeduplicator
{
    private const int MinChineseOverlap = 3;

    public static TextMergeResult MergeOverlap(string previousText, string newText)
    {
        ArgumentNullException.ThrowIfNull(previousText);
        ArgumentNullException.ThrowIfNull(newText);

        var overlapLength = FindLongestOverlap(previousText, newText);
        if (overlapLength < MinChineseOverlap)
        {
            return new TextMergeResult(newText, newText, false, 0);
        }

        var appendedForMerge = newText[overlapLength..];
        return new TextMergeResult(
            previousText + appendedForMerge,
            appendedForMerge.TrimStart(),
            true,
            overlapLength);
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
}

public sealed record TextMergeResult(
    string MergedText,
    string AppendedText,
    bool Deduplicated,
    int OverlapLength);
