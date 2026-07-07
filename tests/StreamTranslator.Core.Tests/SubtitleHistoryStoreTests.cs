using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SubtitleHistoryStoreTests
{
    [TestMethod]
    public async Task AppendAsync_WritesSubtitleAsJsonLine()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-subtitles-");
        var store = new SubtitleHistoryStore(directory.FullName);

        await store.AppendAsync(new SubtitleItem
        {
            Sequence = 1,
            Start = TimeSpan.FromMilliseconds(100),
            End = TimeSpan.FromMilliseconds(900),
            SourceText = "测试字幕",
            Status = SubtitleStatus.Final
        }, new DateOnly(2026, 7, 7));

        var path = Path.Combine(directory.FullName, "2026-07-07.jsonl");
        var line = File.ReadAllText(path);

        StringAssert.Contains(line, "\"sourceText\":\"测试字幕\"");
        StringAssert.Contains(line, "\"status\":\"Final\"");
    }
}

