using StreamTranslator.Core.Configuration;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public async Task LoadAsync_CreatesDefaultSettingsWhenFileIsMissing()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-settings-");
        var settingsPath = Path.Combine(directory.FullName, "settings.json");
        var store = new SettingsStore(settingsPath);

        var settings = await store.LoadAsync();

        Assert.AreEqual(3, settings.SchemaVersion);
        Assert.AreEqual(VadEndpointMode.Balanced, settings.Vad.EndpointMode);
        Assert.AreEqual(400, settings.Vad.EndSilenceMs);
        Assert.AreEqual("https://api.xiaomimimo.com/v1", settings.Asr.BaseUrl);
        Assert.AreEqual("mimo-v2.5-asr", settings.Asr.Model);
        Assert.IsFalse(settings.Translation.Enabled);
        Assert.AreEqual("zh-Hans", settings.Translation.TargetLanguage);
        Assert.IsNull(settings.Translation.ActiveProfileId);
        Assert.AreEqual(0, settings.Translation.Profiles.Count);
        Assert.AreEqual(18d, settings.SubtitleWindow.FontSize);
        Assert.AreEqual(2, settings.SubtitleWindow.MaxSubtitleItems);
        Assert.IsTrue(File.Exists(settingsPath));
    }

    [TestMethod]
    public async Task LoadAsync_ForcesLegacySettingsToBalancedAdaptiveMode()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-settings-");
        var settingsPath = Path.Combine(directory.FullName, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "vad": {
                "endSilenceMs": 250
              },
              "asr": {
                "apiKey": "legacy-key"
              }
            }
            """);
        var store = new SettingsStore(settingsPath);

        var settings = await store.LoadAsync();

        Assert.AreEqual(3, settings.SchemaVersion);
        Assert.AreEqual(VadEndpointMode.Balanced, settings.Vad.EndpointMode);
        Assert.AreEqual(400, settings.Vad.EndSilenceMs);
        Assert.AreEqual("legacy-key", settings.Asr.ApiKey);

        var rewritten = await File.ReadAllTextAsync(settingsPath);
        StringAssert.Contains(rewritten, "\"schemaVersion\": 3");
        StringAssert.Contains(rewritten, "\"endpointMode\": \"Balanced\"");
    }

    [TestMethod]
    public async Task LoadAsync_MigratesSchemaV2SubtitleWindowAndPreservesExistingSettings()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-settings-");
        var settingsPath = Path.Combine(directory.FullName, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "schemaVersion": 2,
              "audio": { "deviceId": "device-42", "followDefaultDevice": false },
              "vad": { "endpointMode": "SentenceComplete", "endSilenceMs": 650 },
              "asr": { "apiKey": "preserved", "language": "en" },
              "subtitleWindow": { "fontSize": 44, "maxLines": 7, "opacity": 0.81 },
              "diagnostics": { "enabled": true }
            }
            """);

        var settings = await new SettingsStore(settingsPath).LoadAsync();

        Assert.AreEqual(3, settings.SchemaVersion);
        Assert.AreEqual("device-42", settings.Audio.DeviceId);
        Assert.AreEqual(VadEndpointMode.SentenceComplete, settings.Vad.EndpointMode);
        Assert.AreEqual(650, settings.Vad.EndSilenceMs);
        Assert.AreEqual("preserved", settings.Asr.ApiKey);
        Assert.AreEqual("en", settings.Asr.Language);
        Assert.AreEqual(18d, settings.SubtitleWindow.FontSize);
        Assert.AreEqual(3, settings.SubtitleWindow.MaxSubtitleItems);
        Assert.AreEqual(0.81, settings.SubtitleWindow.Opacity);
        Assert.IsTrue(settings.Diagnostics.Enabled);
    }
}
