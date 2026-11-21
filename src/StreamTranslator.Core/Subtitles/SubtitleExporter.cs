using System.Globalization;
using System.Text;

namespace StreamTranslator.Core.Subtitles;

public enum SubtitleExportFormat
{
    Srt,
    Text
}

public enum SubtitleExportContentMode
{
    Bilingual,
    SourceOnly,
    TranslationOnly
}

/// <summary>
/// Exports finalized subtitle history records into standard SubRip (.srt) or
/// plain text (.txt) formats. Automatically handles multi-session recordings
/// on the same day so exported SRT cues remain strictly monotonic and non-overlapping.
/// </summary>
public static class SubtitleExporter
{
    private static readonly TimeSpan MinimumCueDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultCueDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionGapPadding = TimeSpan.FromMilliseconds(500);

    public static Encoding Utf8WithBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Export(
        IEnumerable<SubtitleItem> chronologicalItems,
        SubtitleExportFormat format,
        SubtitleExportContentMode contentMode = SubtitleExportContentMode.Bilingual)
    {
        ArgumentNullException.ThrowIfNull(chronologicalItems);

        return format switch
        {
            SubtitleExportFormat.Srt => ExportSrt(chronologicalItems, contentMode),
            SubtitleExportFormat.Text => ExportText(chronologicalItems, contentMode),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    public static string ExportText(
        IEnumerable<SubtitleItem> chronologicalItems,
        SubtitleExportContentMode contentMode = SubtitleExportContentMode.Bilingual)
    {
        ArgumentNullException.ThrowIfNull(chronologicalItems);

        var blocks = new List<string>();
        foreach (var item in chronologicalItems)
        {
            if (item is null || item.Status == SubtitleStatus.NoSpeech)
            {
                continue;
            }

            var lines = SelectLines(item, contentMode);
            if (lines.Count == 0)
            {
                continue;
            }

            var block = new StringBuilder();
            block.Append('[').Append(item.GeneratedTimeText).Append("] ").Append(lines[0]);
            for (var i = 1; i < lines.Count; i++)
            {
                block.AppendLine();
                block.Append("           ").Append(lines[i]);
            }

            blocks.Add(block.ToString());
        }

        return blocks.Count == 0
            ? ""
            : string.Join(Environment.NewLine + Environment.NewLine, blocks) + Environment.NewLine;
    }

    public static string ExportSrt(
        IEnumerable<SubtitleItem> chronologicalItems,
        SubtitleExportContentMode contentMode = SubtitleExportContentMode.Bilingual)
    {
        ArgumentNullException.ThrowIfNull(chronologicalItems);

        var valid = new List<(SubtitleItem Item, IReadOnlyList<string> Lines)>();
        foreach (var item in chronologicalItems)
        {
            if (item is null || item.Status == SubtitleStatus.NoSpeech)
            {
                continue;
            }

            var lines = SelectLines(item, contentMode);
            if (lines.Count > 0)
            {
                valid.Add((item, lines));
            }
        }

        if (valid.Count == 0)
        {
            return "";
        }

        var cues = ComputeMonotonicCues(valid);
        var builder = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatSrtTimestamp(cue.Start))
                   .Append(" --> ")
                   .AppendLine(FormatSrtTimestamp(cue.End));
            foreach (var line in cue.Lines)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    private static List<SrtCue> ComputeMonotonicCues(IReadOnlyList<(SubtitleItem Item, IReadOnlyList<string> Lines)> entries)
    {
        var cues = new List<SrtCue>(entries.Count);
        var sessionOffset = TimeSpan.Zero;
        var previousRawStart = TimeSpan.Zero;
        var previousEnd = TimeSpan.Zero;
        DateTimeOffset? firstGeneratedAt = entries[0].Item.GeneratedAt;

        for (var i = 0; i < entries.Count; i++)
        {
            var (item, lines) = entries[i];
            var hasRelativeTimeline = item.End > item.Start && item.End > TimeSpan.Zero;

            TimeSpan start;
            TimeSpan end;

            if (hasRelativeTimeline)
            {
                // Detect session restart (a new capture session started later the same day
                // whose relative audio clock reset back to ~00:00:00).
                if (i > 0 && item.Start + sessionOffset < previousRawStart)
                {
                    sessionOffset = previousEnd + SessionGapPadding - item.Start;
                    if (sessionOffset < TimeSpan.Zero)
                    {
                        sessionOffset = TimeSpan.Zero;
                    }
                }

                start = item.Start + sessionOffset;
                var duration = item.End - item.Start;
                if (duration < MinimumCueDuration)
                {
                    duration = MinimumCueDuration;
                }

                end = start + duration;
                previousRawStart = start;
            }
            else if (firstGeneratedAt is not null && item.GeneratedAt is not null)
            {
                start = item.GeneratedAt.Value - firstGeneratedAt.Value;
                if (start < TimeSpan.Zero)
                {
                    start = previousEnd;
                }

                end = start + DefaultCueDuration;
            }
            else
            {
                start = i == 0 ? TimeSpan.Zero : previousEnd + SessionGapPadding;
                end = start + DefaultCueDuration;
            }

            if (start < TimeSpan.Zero)
            {
                start = TimeSpan.Zero;
            }

            // Ensure strictly monotonic non-overlapping cues.
            if (i > 0 && start < previousEnd)
            {
                var prev = cues[i - 1];
                if (start > prev.Start + MinimumCueDuration)
                {
                    cues[i - 1] = prev with { End = start };
                }
                else
                {
                    start = previousEnd;
                    if (end < start + MinimumCueDuration)
                    {
                        end = start + MinimumCueDuration;
                    }
                }
            }

            if (end < start + MinimumCueDuration)
            {
                end = start + MinimumCueDuration;
            }

            cues.Add(new SrtCue(start, end, lines));
            previousEnd = end;
        }

        return cues;
    }

    private static IReadOnlyList<string> SelectLines(SubtitleItem item, SubtitleExportContentMode mode)
    {
        var source = NormalizeSingleBlock(item.SourceText);
        var translated = NormalizeSingleBlock(item.TranslatedText);

        return mode switch
        {
            SubtitleExportContentMode.SourceOnly =>
                source.Length > 0 ? [source] : [],
            SubtitleExportContentMode.TranslationOnly =>
                translated.Length > 0 ? [translated] : (source.Length > 0 ? [source] : []),
            _ => (source.Length, translated.Length) switch
            {
                (> 0, > 0) => [source, translated],
                (> 0, 0) => [source],
                (0, > 0) => [translated],
                _ => []
            }
        };
    }

    private static string NormalizeSingleBlock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Trim();
    }

    internal static string FormatSrtTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var totalHours = (int)value.TotalHours;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            totalHours,
            value.Minutes,
            value.Seconds,
            value.Milliseconds);
    }

    private readonly record struct SrtCue(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Lines);
}
