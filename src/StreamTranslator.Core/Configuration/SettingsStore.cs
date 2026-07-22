using System.Text.Json;

namespace StreamTranslator.Core.Configuration;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var existingVersion = 1;
        if (document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
            schemaVersion.TryGetInt32(out var parsedVersion))
        {
            existingVersion = parsedVersion;
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

        if (existingVersion < 2)
        {
            settings = settings with
            {
                Vad = settings.Vad with
                {
                    EndpointMode = VadEndpointMode.Balanced,
                    EndSilenceMs = 400
                }
            };
        }

        if (existingVersion < 3)
        {
            var legacyMaxLines = ReadLegacyMaxLines(document.RootElement);
            settings = settings with
            {
                SchemaVersion = 3,
                Translation = new TranslationSettings(),
                SubtitleWindow = settings.SubtitleWindow with
                {
                    FontSize = 18,
                    MaxSubtitleItems = Math.Clamp(legacyMaxLines ?? 2, 1, 3)
                }
            };
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        return settings;
    }

    private static int? ReadLegacyMaxLines(JsonElement root)
    {
        if (root.TryGetProperty("subtitleWindow", out var subtitleWindow) &&
            subtitleWindow.TryGetProperty("maxLines", out var maxLines) &&
            maxLines.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
