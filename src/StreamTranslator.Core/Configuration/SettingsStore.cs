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

        try
        {
            return await LoadExistingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A corrupt settings file (e.g. truncated by a crash mid-write) must
            // not block startup forever; keep the evidence and start fresh.
            var backupPath = $"{_settingsPath}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
            File.Move(_settingsPath, backupPath, overwrite: true);
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
    }

    private async Task<AppSettings> LoadExistingAsync(CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var existingVersion = 1;
        if (document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
            schemaVersion.TryGetInt32(out var parsedVersion))
        {
            existingVersion = parsedVersion;
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        var shouldSave = false;

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
            shouldSave = true;
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
            shouldSave = true;
        }

        if (existingVersion < 4 ||
            settings.SchemaVersion != 4 ||
            !string.Equals(settings.Asr.Language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            settings = settings with
            {
                SchemaVersion = 4,
                Asr = settings.Asr with { Language = "auto" }
            };
            shouldSave = true;
        }

        if (existingVersion < 5 || settings.SchemaVersion < 5)
        {
            settings = settings with
            {
                SchemaVersion = 5,
                Translation = settings.Translation with
                {
                    SentenceAccumulationLimit = settings.Translation.SentenceAccumulationLimit == 0
                        ? 350
                        : settings.Translation.SentenceAccumulationLimit
                }
            };
            shouldSave = true;
        }

        if (shouldSave)
        {
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

        // Write-then-rename so a crash mid-write can never truncate the live
        // settings file (File.Move with overwrite is atomic on NTFS).
        var tempPath = $"{_settingsPath}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _settingsPath, overwrite: true);
    }
}
