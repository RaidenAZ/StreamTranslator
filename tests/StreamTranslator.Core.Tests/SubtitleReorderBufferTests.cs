using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SubtitleReorderBufferTests
{
    [TestMethod]
    public void Add_ReleasesItemsInSequenceOrder()
    {
        var buffer = new SubtitleReorderBuffer(firstSequence: 1);
        var second = Item(2, "第二句");
        var first = Item(1, "第一句");

        Assert.AreEqual(0, buffer.Add(second).Count);

        var released = buffer.Add(first);

        Assert.AreEqual(2, released.Count);
        Assert.AreEqual("第一句", released[0].SourceText);
        Assert.AreEqual("第二句", released[1].SourceText);
    }

    private static SubtitleItem Item(long sequence, string text)
    {
        return new SubtitleItem
        {
            Sequence = sequence,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(1),
            SourceText = text,
            Status = SubtitleStatus.Final
        };
    }
}

