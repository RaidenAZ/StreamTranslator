using StreamTranslator.Core.Subtitles;
using System.Text.Json;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SubtitleHistoryStoreTests
{
    [TestMethod]
    public void GeneratedTimeText_UsesRecordedSystemClockTime()
    {
        var item = new SubtitleItem
        {
            GeneratedAt = new DateTimeOffset(2026, 7, 14, 1, 23, 45, TimeSpan.FromHours(8))
        };

        Assert.AreEqual("01:23:45", item.GeneratedTimeText);
    }

    [TestMethod]
    public void GeneratedTimeText_UsesFallbackForLegacyHistory()
    {
        var item = new SubtitleItem();

        Assert.AreEqual("--:--:--", item.GeneratedTimeText);
    }

    [TestMethod]
    public async Task AppendAsync_WritesSubtitleAsJsonLine()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-subtitles-");
        var store = new SubtitleHistoryStore(directory.FullName);

        var generatedAt = new DateTimeOffset(2026, 7, 7, 9, 8, 7, TimeSpan.FromHours(8));
        await store.AppendAsync(new SubtitleItem
        {
            Sequence = 1,
            Start = TimeSpan.FromMilliseconds(100),
            End = TimeSpan.FromMilliseconds(900),
            GeneratedAt = generatedAt,
            SourceText = "测试字幕",
            Status = SubtitleStatus.Final
        }, new DateOnly(2026, 7, 7));

        var path = Path.Combine(directory.FullName, "2026-07-07.jsonl");
        var line = File.ReadAllText(path);

        StringAssert.Contains(line, "\"sourceText\":\"测试字幕\"");
        StringAssert.Contains(line, "\"status\":\"Final\"");

        using var document = JsonDocument.Parse(line);
        Assert.AreEqual(generatedAt, document.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
    }
}
