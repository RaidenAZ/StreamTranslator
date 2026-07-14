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
        var isCurrentSchema = document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
                              schemaVersion.TryGetInt32(out var version) &&
                              version >= 2;
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

        if (!isCurrentSchema)
        {
            settings = settings with
            {
                SchemaVersion = 2,
                Vad = settings.Vad with
                {
                    EndpointMode = VadEndpointMode.Balanced,
                    EndSilenceMs = 400
                }
            };
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        return settings;
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
