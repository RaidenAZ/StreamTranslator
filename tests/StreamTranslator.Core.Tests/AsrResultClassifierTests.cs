using StreamTranslator.Core.Subtitles;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class AsrResultClassifierTests
{
    [TestMethod]
    public void Classify_SuccessWithTextIsFinal()
    {
        Assert.AreEqual(SubtitleStatus.Final, AsrResultClassifier.Classify(Success("嗯。")));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Classify_SuccessWithoutTextIsNoSpeech(string? text)
    {
        Assert.AreEqual(SubtitleStatus.NoSpeech, AsrResultClassifier.Classify(Success(text)));
    }

    [TestMethod]
    public void Classify_ErrorIsFailedEvenWhenTextIsEmpty()
    {
        var response = new WorkerResponse
        {
            Id = "seg-1",
            Type = "error",
            Ok = false,
            ErrorKind = "network",
            Retryable = true
        };

        Assert.AreEqual(SubtitleStatus.Failed, AsrResultClassifier.Classify(response));
    }

    [TestMethod]
    public void NoSpeechSuccessDoesNotStopOrRetry()
    {
        Assert.AreEqual(WorkerFailureAction.None, WorkerFailurePolicy.Decide(Success(""), attempt: 0));
    }

    private static WorkerResponse Success(string? text)
    {
        return new WorkerResponse
        {
            Id = "seg-1",
            Type = "transcribe_result",
            Ok = true,
            Text = text,
            LatencyMs = 600
        };
    }
}
