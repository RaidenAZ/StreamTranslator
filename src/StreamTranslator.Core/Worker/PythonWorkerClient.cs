namespace StreamTranslator.Core.Worker;

public sealed class PythonWorkerClient : JsonLinesWorkerClient<WorkerResponse>
{
    public PythonWorkerClient(
        string executablePath,
        string arguments,
        IReadOnlyDictionary<string, string> environment,
        string? stderrLogPath = null,
        TimeSpan? requestTimeout = null)
        : base(executablePath, arguments, environment, stderrLogPath, requestTimeout)
    {
    }

    protected override string WorkerName => "ASR worker";

    protected override string GetResponseId(WorkerResponse response)
    {
        return response.Id;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsProcessRunning)
        {
            return;
        }

        StartProcess();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var request = WorkerRequest.Ping($"ping-{Guid.NewGuid():N}");
            var response = await SendAsync(request.Id, request, timeout.Token).ConfigureAwait(false);

            if (!response.Ok)
            {
                throw new InvalidOperationException(response.ErrorMessage ?? "ASR worker health check failed.");
            }
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<WorkerResponse> TranscribeAsync(WorkerRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Type != WorkerMessageTypes.Transcribe)
        {
            throw new ArgumentException("Only transcribe requests are allowed.", nameof(request));
        }

        return SendAsync(request.Id, request, cancellationToken);
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!HasProcess)
        {
            return;
        }

        try
        {
            if (IsProcessRunning)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var request = WorkerRequest.Shutdown($"shutdown-{Guid.NewGuid():N}");
                await SendAsync(request.Id, request, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Shutdown is best-effort; the process tree is killed below if graceful exit fails.
        }

        await StopProcessAsync().ConfigureAwait(false);
    }
}
