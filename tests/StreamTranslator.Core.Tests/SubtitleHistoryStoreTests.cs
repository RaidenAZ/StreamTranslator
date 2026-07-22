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
    public async Task LoadLatestAsync_MaterializesTranslationForCurrentSourceRevision()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-history-");
        var store = new SubtitleHistoryStore(directory.FullName);
        var date = new DateOnly(2026, 7, 14);
        var source = Item(1, "Hello") with
        {
            UtteranceGroupId = "session:1",
            Revision = 1,
            GeneratedAt = DateTimeOffset.Parse("2026-07-14T01:23:45+08:00")
        };
        await store.AppendAsync(source, date);
        await store.AppendTranslationResultAsync(new TranslationHistoryEvent
        {
            UtteranceGroupId = "session:1",
            SourceRevision = 1,
            TargetLanguage = "zh-Hans",
            TranslatedText = "你好",
            TranslationProfileId = Guid.NewGuid(),
            Model = "model",
            CompletedAt = DateTimeOffset.Parse("2026-07-14T01:23:46+08:00")
        }, date);

        var loaded = await store.LoadLatestAsync(date);

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("Hello", loaded[0].SourceText);
        Assert.AreEqual("你好", loaded[0].TranslatedText);
        Assert.AreEqual("01:23:45", loaded[0].GeneratedTimeText);
    }

    [TestMethod]
    public async Task LoadLatestAsync_IgnoresStaleTranslationAndCorruptTranslationEvent()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-history-");
        var store = new SubtitleHistoryStore(directory.FullName);
        var date = new DateOnly(2026, 7, 14);
        var first = Item(1, "Hello") with { UtteranceGroupId = "session:1", Revision = 1 };
        var revision = Item(1, "Hello again") with
        {
            Type = "subtitle_revision",
            UtteranceGroupId = "session:1",
            Revision = 2,
            ReplacesSequences = [1, 2]
        };
        await store.AppendAsync(first, date);
        await store.AppendRevisionAsync(revision, date);
        await store.AppendTranslationResultAsync(new TranslationHistoryEvent
        {
            UtteranceGroupId = "session:1",
            SourceRevision = 1,
            TargetLanguage = "zh-Hans",
            TranslatedText = "过期译文",
            TranslationProfileId = Guid.NewGuid(),
            Model = "model",
            CompletedAt = DateTimeOffset.Now
        }, date);
        await File.AppendAllTextAsync(
            Path.Combine(directory.FullName, "2026-07-14.jsonl"),
            "{\"type\":\"translation_result\",broken}" + Environment.NewLine);

        var loaded = await store.LoadLatestAsync(date);

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("Hello again", loaded[0].SourceText);
        Assert.IsNull(loaded[0].TranslatedText);
    }

    [TestMethod]
    public async Task TranslationStatus_DoesNotOverwriteSuccessfulTranslation()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-history-");
        var store = new SubtitleHistoryStore(directory.FullName);
        var date = new DateOnly(2026, 7, 14);
        await store.AppendAsync(Item(1, "Hello") with { UtteranceGroupId = "session:1" }, date);
        await store.AppendTranslationResultAsync(new TranslationHistoryEvent
        {
            UtteranceGroupId = "session:1",
            SourceRevision = 1,
            TargetLanguage = "zh-Hans",
            TranslatedText = "你好",
            TranslationProfileId = Guid.NewGuid(),
            Model = "model",
            CompletedAt = DateTimeOffset.Now
        }, date);
        await store.AppendTranslationStatusAsync(new TranslationStatusHistoryEvent
        {
            UtteranceGroupId = "session:1",
            SourceRevision = 1,
            Status = "translation_failed",
            CompletedAt = DateTimeOffset.Now
        }, date);

        var loaded = await store.LoadLatestAsync(date);

        Assert.AreEqual("你好", loaded[0].TranslatedText);
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

    private static SubtitleItem Item(long sequence, string text)
    {
        return HistoryItem(sequence, text, $"session:{sequence}");
    }

}
