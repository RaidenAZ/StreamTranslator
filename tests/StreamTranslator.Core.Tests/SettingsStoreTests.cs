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

        Assert.AreEqual(2, settings.SchemaVersion);
        Assert.AreEqual(VadEndpointMode.Balanced, settings.Vad.EndpointMode);
        Assert.AreEqual(400, settings.Vad.EndSilenceMs);
        Assert.AreEqual("https://api.xiaomimimo.com/v1", settings.Asr.BaseUrl);
        Assert.AreEqual("mimo-v2.5-asr", settings.Asr.Model);
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

        Assert.AreEqual(2, settings.SchemaVersion);
        Assert.AreEqual(VadEndpointMode.Balanced, settings.Vad.EndpointMode);
        Assert.AreEqual(400, settings.Vad.EndSilenceMs);
        Assert.AreEqual("legacy-key", settings.Asr.ApiKey);

        var rewritten = await File.ReadAllTextAsync(settingsPath);
        StringAssert.Contains(rewritten, "\"schemaVersion\": 2");
        StringAssert.Contains(rewritten, "\"endpointMode\": \"Balanced\"");
    }
}
