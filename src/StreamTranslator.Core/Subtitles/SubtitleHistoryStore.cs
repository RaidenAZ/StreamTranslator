using System.Text.Json;
using System.Text.Encodings.Web;

namespace StreamTranslator.Core.Subtitles;

public sealed class SubtitleHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
}
