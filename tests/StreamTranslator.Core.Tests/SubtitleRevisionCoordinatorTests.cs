using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class SubtitleRevisionCoordinatorTests
{
    [TestMethod]
    public void Publish_ProducesRevisionForAdjacentItemsInSameGroup()
    {
        var coordinator = new SubtitleRevisionCoordinator();
        var first = Item(1, "Welcome to the show", "utt-000001");
        var second = Item(2, "the show today", "utt-000001");

        var firstPublication = coordinator.Publish(first);
        var secondPublication = coordinator.Publish(second);

        Assert.AreEqual(SubtitlePublicationKind.Append, firstPublication.Kind);
        Assert.AreEqual("subtitle", firstPublication.Item.Type);
        Assert.AreEqual(1, firstPublication.Item.Revision);
        CollectionAssert.AreEqual(new long[] { 1 }, firstPublication.Item.ReplacesSequences);

        Assert.AreEqual(SubtitlePublicationKind.Revise, secondPublication.Kind);
        Assert.AreEqual("subtitle_revision", secondPublication.Item.Type);
        Assert.AreEqual(2, secondPublication.Item.Revision);
        Assert.AreEqual("Welcome to the show today", secondPublication.Item.SourceText);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, secondPublication.Item.ReplacesSequences);
    }

    [TestMethod]
    public void Publish_AsrFailureBreaksRevisionChain()
    {
        var coordinator = new SubtitleRevisionCoordinator();
        coordinator.Publish(Item(1, "first", "utt-000001"));
        var failed = coordinator.Publish(Item(2, "识别失败", "utt-000001") with { Status = SubtitleStatus.Failed });
        var third = coordinator.Publish(Item(3, "third", "utt-000001"));

        Assert.AreEqual(SubtitlePublicationKind.Append, failed.Kind);
        Assert.AreEqual(SubtitlePublicationKind.Append, third.Kind);
        Assert.AreEqual("third", third.Item.SourceText);
        Assert.AreEqual(1, third.Item.Revision);
    }

    [TestMethod]
    public void Publish_DoesNotReviseBeyondThreeSegments()
    {
        var coordinator = new SubtitleRevisionCoordinator();

        coordinator.Publish(Item(1, "one", "utt-000001"));
        coordinator.Publish(Item(2, "two", "utt-000001"));
        var third = coordinator.Publish(Item(3, "three", "utt-000001"));
        var fourth = coordinator.Publish(Item(4, "four", "utt-000001"));

        Assert.AreEqual(SubtitlePublicationKind.Revise, third.Kind);
        Assert.AreEqual(3, third.Item.Revision);
        Assert.AreEqual(SubtitlePublicationKind.Append, fourth.Kind);
        Assert.AreEqual(1, fourth.Item.Revision);
    }

    private static SubtitleItem Item(long sequence, string text, string groupId)
    {
        return new SubtitleItem
        {
            Sequence = sequence,
            UtteranceGroupId = groupId,
            SourceText = text,
            Status = SubtitleStatus.Final
        };
    }
}
