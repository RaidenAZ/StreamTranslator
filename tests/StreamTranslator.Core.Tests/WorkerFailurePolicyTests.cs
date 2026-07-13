using StreamTranslator.Core.Worker;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class WorkerFailurePolicyTests
{
    [TestMethod]
    public void Decide_StopsForAuthenticationFailure()
    {
        var response = Failure("authentication", statusCode: 401, retryable: false);

        Assert.AreEqual(WorkerFailureAction.StopRuntime, WorkerFailurePolicy.Decide(response, attempt: 0));
    }

    [TestMethod]
    public void Decide_RetriesRecoverableFailureOnlyOnce()
    {
        var response = Failure("rate_limit", statusCode: 429, retryable: true);

        Assert.AreEqual(WorkerFailureAction.Retry, WorkerFailurePolicy.Decide(response, attempt: 0));
        Assert.AreEqual(WorkerFailureAction.FailSegment, WorkerFailurePolicy.Decide(response, attempt: 1));
    }

    private static WorkerResponse Failure(string kind, int statusCode, bool retryable)
    {
        return new WorkerResponse
        {
            Id = "seg-1",
            Type = "error",
            Ok = false,
            ErrorKind = kind,
            StatusCode = statusCode,
            Retryable = retryable
        };
    }
}
