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

        Assert.AreEqual(300, settings.Vad.EndSilenceMs);
        Assert.AreEqual("https://api.xiaomimimo.com/v1", settings.Asr.BaseUrl);
        Assert.AreEqual("mimo-v2.5-asr", settings.Asr.Model);
        Assert.IsTrue(File.Exists(settingsPath));
    }
}
