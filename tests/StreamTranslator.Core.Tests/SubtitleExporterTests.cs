using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SubtitleExporterTests
{
    [TestMethod]
    public void ExportSrt_FormatsBilingualCuesWithValidTimecodes()
    {
        var items = new[]
        {
            new SubtitleItem
            {
                Sequence = 1,
                Start = TimeSpan.FromSeconds(1.25),
                End = TimeSpan.FromSeconds(4.5),
                SourceText = "Welcome to the stream.",
                TranslatedText = "欢迎来到直播间。",
                Status = SubtitleStatus.Final
            },
            new SubtitleItem
            {
                Sequence = 2,
                Start = TimeSpan.FromSeconds(5.0),
                End = TimeSpan.FromSeconds(8.2),
                SourceText = "Let's get started.",
                TranslatedText = "我们开始吧。",
                Status = SubtitleStatus.Final
            }
        };

        var srt = SubtitleExporter.ExportSrt(items, SubtitleExportContentMode.Bilingual);

        StringAssert.Contains(srt, "1" + Environment.NewLine + "00:00:01,250 --> 00:00:04,500");
        StringAssert.Contains(srt, "Welcome to the stream." + Environment.NewLine + "欢迎来到直播间。");
        StringAssert.Contains(srt, "2" + Environment.NewLine + "00:00:05,000 --> 00:00:08,200");
        StringAssert.Contains(srt, "Let's get started." + Environment.NewLine + "我们开始吧。");
    }

    [TestMethod]
    public void ExportSrt_OffsetsMultipleSessionsMonotonicallyWithoutOverlap()
    {
        var items = new[]
        {
            new SubtitleItem
            {
                Sequence = 1,
                Start = TimeSpan.FromSeconds(2),
                End = TimeSpan.FromSeconds(6),
                SourceText = "Session one.",
                Status = SubtitleStatus.Final
            },
            // Second session restarted later the same day: relative Start reset to 1s.
            new SubtitleItem
            {
                Sequence = 1,
                Start = TimeSpan.FromSeconds(1),
                End = TimeSpan.FromSeconds(4),
                SourceText = "Session two.",
                Status = SubtitleStatus.Final
            }
        };

        var srt = SubtitleExporter.ExportSrt(items, SubtitleExportContentMode.SourceOnly);

        StringAssert.Contains(srt, "00:00:02,000 --> 00:00:06,000");
        // Second session starts cleanly after 00:00:06,000 + 500ms padding = 00:00:06,500 -> 00:00:09,500.
        StringAssert.Contains(srt, "00:00:06,500 --> 00:00:09,500");
    }

    [TestMethod]
    public void ExportText_RespectsContentModes()
    {
        var time = new DateTimeOffset(2026, 11, 21, 20, 15, 30, TimeSpan.FromHours(8));
        var items = new[]
        {
            new SubtitleItem
            {
                Sequence = 1,
                GeneratedAt = time,
                SourceText = "Hello world",
                TranslatedText = "你好世界",
                Status = SubtitleStatus.Final
            }
        };

        var bilingual = SubtitleExporter.ExportText(items, SubtitleExportContentMode.Bilingual);
        var sourceOnly = SubtitleExporter.ExportText(items, SubtitleExportContentMode.SourceOnly);
        var translationOnly = SubtitleExporter.ExportText(items, SubtitleExportContentMode.TranslationOnly);

        StringAssert.Contains(bilingual, "[20:15:30] Hello world");
        StringAssert.Contains(bilingual, "你好世界");
        StringAssert.Contains(sourceOnly, "[20:15:30] Hello world");
        Assert.IsFalse(sourceOnly.Contains("你好世界", StringComparison.Ordinal));
        StringAssert.Contains(translationOnly, "[20:15:30] 你好世界");
        Assert.IsFalse(translationOnly.Contains("Hello world", StringComparison.Ordinal));
    }
}
