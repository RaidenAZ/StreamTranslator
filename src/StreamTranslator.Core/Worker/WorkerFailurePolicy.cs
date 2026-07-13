namespace StreamTranslator.Core.Worker;

public enum WorkerFailureAction
{
    None,
    Retry,
    FailSegment,
    StopRuntime
}

public static class WorkerFailurePolicy
{
    public static WorkerFailureAction Decide(WorkerResponse response, int attempt)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Ok)
        {
            return WorkerFailureAction.None;
        }

        if (response.StatusCode is 401 or 403 ||
            string.Equals(response.ErrorKind, "authentication", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerFailureAction.StopRuntime;
        }

        return response.Retryable && attempt == 0
            ? WorkerFailureAction.Retry
            : WorkerFailureAction.FailSegment;
    }
}
