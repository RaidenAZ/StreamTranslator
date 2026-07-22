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
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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

        await AppendEventAsync(item, date, cancellationToken).ConfigureAwait(false);
    }

    public Task AppendTranslationResultAsync(
        TranslationHistoryEvent result,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.UtteranceGroupId) ||
            result.SourceRevision < 1 ||
            string.IsNullOrWhiteSpace(result.TranslatedText))
        {
            throw new ArgumentException("Translation result metadata is incomplete.", nameof(result));
        }

        return AppendEventAsync(result, date, cancellationToken);
    }

    public Task AppendTranslationStatusAsync(
        TranslationStatusHistoryEvent status,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (string.IsNullOrWhiteSpace(status.UtteranceGroupId) ||
            status.SourceRevision < 1 ||
            string.IsNullOrWhiteSpace(status.Status))
        {
            throw new ArgumentException("Translation status metadata is incomplete.", nameof(status));
        }

        return AppendEventAsync(status, date, cancellationToken);
    }

    private async Task AppendEventAsync<T>(
        T value,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{date:yyyy-MM-dd}.jsonl");
        var line = JsonSerializer.Serialize(value, JsonOptions);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var type = document.RootElement.TryGetProperty("type", out var typeProperty)
                    ? typeProperty.GetString()
                    : "subtitle";
                if (string.Equals(type, "translation_result", StringComparison.Ordinal))
                {
                    TranslationHistoryEvent? translation;
                    try
                    {
                        translation = document.RootElement.Deserialize<TranslationHistoryEvent>(JsonOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (translation is not null)
                    {
                        ApplyTranslation(items, translation);
                    }
                    continue;
                }

                if (string.Equals(type, "translation_status", StringComparison.Ordinal))
                {
                    continue;
                }

                SubtitleItem? item;
                try
                {
                    item = document.RootElement.Deserialize<SubtitleItem>(JsonOptions);
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
        }

        return items;
    }

    private static void ApplyTranslation(IList<SubtitleItem> items, TranslationHistoryEvent translation)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var source = items[index];
            if (string.Equals(source.UtteranceGroupId, translation.UtteranceGroupId, StringComparison.Ordinal) &&
                source.Revision == translation.SourceRevision)
            {
                items[index] = source with { TranslatedText = translation.TranslatedText };
                return;
            }
        }
    }
}

public sealed record TranslationHistoryEvent
{
    public string Type { get; init; } = "translation_result";
    public string UtteranceGroupId { get; init; } = "";
    public int SourceRevision { get; init; }
    public string TargetLanguage { get; init; } = "";
    public string TranslatedText { get; init; } = "";
    public Guid TranslationProfileId { get; init; }
    public string Model { get; init; } = "";
    public DateTimeOffset CompletedAt { get; init; }
}

public sealed record TranslationStatusHistoryEvent
{
    public string Type { get; init; } = "translation_status";
    public string UtteranceGroupId { get; init; } = "";
    public int SourceRevision { get; init; }
    public string TargetLanguage { get; init; } = "";
    public string Status { get; init; } = "";
    public string? ErrorKind { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}
