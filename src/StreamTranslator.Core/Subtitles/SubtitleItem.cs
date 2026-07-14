using System.Globalization;
using System.Text.Json.Serialization;

namespace StreamTranslator.Core.Subtitles;

public sealed record SubtitleItem
{
    public long Sequence { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public DateTimeOffset? GeneratedAt { get; init; }
    public string SourceText { get; init; } = "";
    public string? TranslatedText { get; init; }
    public SubtitleStatus Status { get; init; }

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
