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

    [TestMethod]
    public async Task LoadLatestAsync_MaterializesNewestAppendOnlyRevision()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-subtitles-");
        var store = new SubtitleHistoryStore(directory.FullName);
        var date = new DateOnly(2026, 7, 14);
        var first = HistoryItem(1, "Welcome to the show", "utt-000001");
        var second = HistoryItem(2, "the show today", "utt-000001");
        var coordinator = new SubtitleRevisionCoordinator();
        coordinator.Publish(first);
        var revision = coordinator.Publish(second);

        await store.AppendAsync(first, date);
        await store.AppendAsync(second, date);
        await store.AppendRevisionAsync(revision.Item, date);

        var latest = await store.LoadLatestAsync(date);

        Assert.AreEqual(1, latest.Count);
        Assert.AreEqual("subtitle_revision", latest[0].Type);
        Assert.AreEqual("Welcome to the show today", latest[0].SourceText);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, latest[0].ReplacesSequences);
    }

    [TestMethod]
    public async Task LoadLatestAsync_DoesNotReplaceSameSequenceFromAnotherSessionGroup()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-subtitles-");
        var store = new SubtitleHistoryStore(directory.FullName);
        var date = new DateOnly(2026, 7, 14);
        var previousSession = HistoryItem(1, "previous session", "session-a-utt-000001");
        var currentFirst = HistoryItem(1, "current", "session-b-utt-000001");
        var currentSecond = HistoryItem(2, "session", "session-b-utt-000001");
        var coordinator = new SubtitleRevisionCoordinator();
        coordinator.Publish(currentFirst);
        var revision = coordinator.Publish(currentSecond).Item;

        await store.AppendAsync(previousSession, date);
        await store.AppendAsync(currentFirst, date);
        await store.AppendAsync(currentSecond, date);
        await store.AppendRevisionAsync(revision, date);

        var latest = await store.LoadLatestAsync(date);

        Assert.AreEqual(2, latest.Count);
        Assert.AreEqual("previous session", latest[0].SourceText);
        Assert.AreEqual("current session", latest[1].SourceText);
    }

    private static SubtitleItem HistoryItem(long sequence, string text, string groupId)
    {
        return new SubtitleItem
        {
            Sequence = sequence,
            UtteranceGroupId = groupId,
            ReplacesSequences = [sequence],
            Start = TimeSpan.FromSeconds(sequence - 1),
            End = TimeSpan.FromSeconds(sequence),
            GeneratedAt = new DateTimeOffset(2026, 7, 14, 1, 23, 40 + (int)sequence, TimeSpan.FromHours(8)),
            SourceText = text,
            Status = SubtitleStatus.Final
        };
    }

}
