using System.Text.Json;
using System.Text.Encodings.Web;

namespace StreamTranslator.Core.Subtitles;

public sealed class SubtitleHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _directory;

    public SubtitleHistoryStore(string directory)
    {
        _directory = directory;
    }

    public async Task AppendAsync(
        SubtitleItem item,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, $"{date:yyyy-MM-dd}.jsonl");
        var line = JsonSerializer.Serialize(item, JsonOptions);
        await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    public Task AppendRevisionAsync(
        SubtitleItem revision,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (!string.Equals(revision.Type, "subtitle_revision", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(revision.UtteranceGroupId) ||
            revision.ReplacesSequences.Length < 2)
        {
            throw new ArgumentException("Subtitle revision metadata is incomplete.", nameof(revision));
        }

        return AppendAsync(revision, date, cancellationToken);
    }

    public async Task<IReadOnlyList<SubtitleItem>> LoadLatestAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, $"{date:yyyy-MM-dd}.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        var items = new List<SubtitleItem>();
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SubtitleItem? item;
            try
            {
                item = JsonSerializer.Deserialize<SubtitleItem>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (item is null)
            {
                continue;
            }

            if (!string.Equals(item.Type, "subtitle_revision", StringComparison.Ordinal) ||
                item.ReplacesSequences.Length == 0)
            {
                items.Add(item);
                continue;
            }

            var indexes = items
                .Select((existing, index) => new { existing, index })
                .Where(entry => SubtitleRevisionCoordinator.Replaces(item, entry.existing))
                .Select(static entry => entry.index)
                .ToArray();
            var insertIndex = indexes.Length == 0 ? items.Count : indexes.Min();
            for (var index = indexes.Length - 1; index >= 0; index--)
            {
                items.RemoveAt(indexes[index]);
            }

            items.Insert(insertIndex, item);
        }

        return items;
    }
}
