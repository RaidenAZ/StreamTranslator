using System.Globalization;
using System.Text.Json.Serialization;

namespace StreamTranslator.Core.Subtitles;

public sealed record SubtitleItem
{
    public string Type { get; init; } = "subtitle";
    public long Sequence { get; init; }
    public string? UtteranceGroupId { get; init; }
    public int Revision { get; init; } = 1;
    public long[] ReplacesSequences { get; init; } = [];
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public DateTimeOffset? GeneratedAt { get; init; }
    public string SourceText { get; init; } = "";
    public string? TranslatedText { get; init; }
    public SubtitleStatus Status { get; init; }
    // Forwarded from CompletedSpeechSegment; used by TextSentenceAccumulator
    // to decide whether to accumulate (HardMax) or flush immediately (other values).
    // Null for items loaded from history or produced before V1.3.
    [JsonIgnore]
    public string? CutReason { get; init; }
    // Tail (~120 chars) of the previous sentence unit's source text, set by
    // TextSentenceAccumulator and forwarded to the translation model as context.
    [JsonIgnore]
    public string? PreviousSourceTail { get; init; }

    [JsonIgnore]
    public string GeneratedTimeText => GeneratedAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--:--:--";
}

[JsonConverter(typeof(JsonStringEnumConverter<SubtitleStatus>))]
public enum SubtitleStatus
{
    Interim,
    Final,
    Failed
}
