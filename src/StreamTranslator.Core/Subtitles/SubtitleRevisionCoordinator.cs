namespace StreamTranslator.Core.Subtitles;

public sealed class SubtitleRevisionCoordinator
{
    private SubtitleItem? _currentItem;

    public SubtitlePublication Publish(SubtitleItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Status != SubtitleStatus.Final || string.IsNullOrWhiteSpace(item.UtteranceGroupId))
        {
            _currentItem = null;
            return new SubtitlePublication(SubtitlePublicationKind.Append, AsInitialItem(item));
        }

        var canRevise = _currentItem is not null &&
                        _currentItem.Status == SubtitleStatus.Final &&
                        string.Equals(
                            _currentItem.UtteranceGroupId,
                            item.UtteranceGroupId,
                            StringComparison.Ordinal) &&
                        item.Sequence == _currentItem.Sequence + 1 &&
                        _currentItem.ReplacesSequences.Length < 3 &&
                        item.End - _currentItem.Start <= TimeSpan.FromSeconds(12);
        if (!canRevise)
        {
            _currentItem = AsInitialItem(item);
            return new SubtitlePublication(SubtitlePublicationKind.Append, _currentItem);
        }

        var currentItem = _currentItem ?? throw new InvalidOperationException("Revision state was not initialized.");
        var replacedSequences = currentItem.ReplacesSequences
            .Append(item.Sequence)
            .Distinct()
            .Order()
            .ToArray();
        var revision = item with
        {
            Type = "subtitle_revision",
            Revision = currentItem.Revision + 1,
            ReplacesSequences = replacedSequences,
            Start = currentItem.Start,
            SourceText = MergeAdjacentText(currentItem.SourceText, item.SourceText)
        };
        _currentItem = revision;
        return new SubtitlePublication(SubtitlePublicationKind.Revise, revision);
    }

    public void CloseCurrentGroup()
    {
        _currentItem = null;
    }

    public static bool Replaces(SubtitleItem revision, SubtitleItem existing)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(existing);
        if (!string.Equals(
                revision.UtteranceGroupId,
                existing.UtteranceGroupId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var replacedSequences = revision.ReplacesSequences;
        return replacedSequences.Contains(existing.Sequence) ||
               existing.ReplacesSequences.Any(replacedSequences.Contains);
    }

    private static SubtitleItem AsInitialItem(SubtitleItem item)
    {
        return item with
        {
            Type = "subtitle",
            Revision = 1,
            ReplacesSequences = [item.Sequence]
        };
    }

    private static string MergeAdjacentText(string previousText, string newText)
    {
        var overlap = TextDeduplicator.MergeOverlap(previousText, newText);
        if (overlap.Deduplicated)
        {
            return overlap.MergedText;
        }

        var left = previousText.TrimEnd();
        var right = newText.TrimStart();
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        var separator = NeedsAsciiWordSeparator(left[^1], right[0]) ? " " : "";
        return left + separator + right;
    }

    private static bool NeedsAsciiWordSeparator(char left, char right)
    {
        return left <= 0x7f && right <= 0x7f &&
               char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);
    }
}

public sealed record SubtitlePublication(SubtitlePublicationKind Kind, SubtitleItem Item);

public enum SubtitlePublicationKind
{
    Append,
    Revise
}
