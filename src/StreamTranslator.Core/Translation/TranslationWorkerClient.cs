using System.Text.RegularExpressions;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.Core.Translation;

public sealed partial class TranslationWorkerClient
    : JsonLinesWorkerClient<TranslationWorkerResponse>, ITranslationWorkerClient
{
    private string _apiKey = "";

    public TranslationWorkerClient(
        string executablePath,
        string arguments,
        string? stderrLogPath = null,
        TimeSpan? requestTimeout = null)
        : base(executablePath, arguments, environment: null, stderrLogPath, requestTimeout)
    {
    }

    protected override string WorkerName => "Translation worker";

    protected override string GetResponseId(TranslationWorkerResponse response)
    {
        return response.Id;
    }

    public async Task<TranslationWorkerResponse> StartAsync(
        TranslationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (IsProcessRunning)
        {
            throw new InvalidOperationException("Translation worker is already running.");
        }

        _apiKey = profile.ApiKey;
        StartProcess();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var request = TranslationWorkerRequest.Configure($"cfg-{Guid.NewGuid():N}", profile);
            var response = await SendAsync(request.Id, request, timeout.Token).ConfigureAwait(false);
            if (!response.Ok || response.Type != TranslationWorkerMessageTypes.Configured)
            {
                throw new InvalidOperationException(response.ErrorMessage ?? "Translation worker configuration failed.");
            }

            return response;
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<TranslationWorkerResponse> TranslateAsync(
        TranslationWorkerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type != TranslationWorkerMessageTypes.Translate)
        {
            throw new ArgumentException("Only translate requests are allowed.", nameof(request));
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
                var request = TranslationWorkerRequest.Shutdown($"shutdown-{Guid.NewGuid():N}");
                await SendAsync(request.Id, request, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Shutdown is best-effort; StopProcessAsync kills a stuck process tree.
        }

        await StopProcessAsync().ConfigureAwait(false);
    }

    protected override string RedactStderrLine(string line)
    {
        var redacted = string.IsNullOrEmpty(_apiKey)
            ? line
            : line.Replace(_apiKey, "***", StringComparison.Ordinal);
        return BearerPattern().Replace(redacted, "Bearer ***");
    }

    protected override void OnProcessStopped()
    {
        _apiKey = "";
    }

    [GeneratedRegex("Bearer\\s+\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();
}
