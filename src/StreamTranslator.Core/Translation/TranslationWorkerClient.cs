using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.Core.Translation;

public sealed partial class TranslationWorkerClient : ITranslationWorkerClient
{
    private readonly string _executablePath;
    private readonly string _arguments;
    private readonly string? _stderrLogPath;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TranslationWorkerResponse>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _stopCts;
    private string _apiKey = "";

    public TranslationWorkerClient(string executablePath, string arguments, string? stderrLogPath = null)
    {
        _executablePath = executablePath;
        _arguments = arguments;
        _stderrLogPath = stderrLogPath;
    }

    public async Task<TranslationWorkerResponse> StartAsync(
        TranslationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_process is { HasExited: false })
        {
            throw new InvalidOperationException("Translation worker is already running.");
        }

        _apiKey = profile.ApiKey;
        _stopCts = new CancellationTokenSource();
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = _arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // The worker protocol is UTF-8 JSON lines; without this the OS ANSI
            // code page (e.g. GBK) is used and non-ASCII text is corrupted.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start translation worker.");
        _stdoutTask = Task.Run(() => ReadStdoutAsync(_stopCts.Token), CancellationToken.None);
        _stderrTask = Task.Run(() => ReadStderrAsync(_stopCts.Token), CancellationToken.None);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await SendAsync(
                    TranslationWorkerRequest.Configure($"cfg-{Guid.NewGuid():N}", profile),
                    timeout.Token)
                .ConfigureAwait(false);
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

        return SendAsync(request, cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await SendAsync(
                        TranslationWorkerRequest.Shutdown($"shutdown-{Guid.NewGuid():N}"),
                        timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Shutdown is best-effort; StopProcessAsync kills a stuck process tree.
        }

        await StopProcessAsync().ConfigureAwait(false);
    }

    private async Task<TranslationWorkerResponse> SendAsync(
        TranslationWorkerRequest request,
        CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Translation worker is not running.");
        if (process.HasExited)
        {
            throw new InvalidOperationException("Translation worker has exited.");
        }

        var completion = new TaskCompletionSource<TranslationWorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, completion))
        {
            throw new InvalidOperationException($"Duplicate translation worker request id: {request.Id}");
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(
                        WorkerJson.Serialize(request).AsMemory(),
                        cancellationToken)
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            _pending.TryRemove(request.Id, out _);
            throw;
        }

        await using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(request.Id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task ReadStdoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            var process = _process;
            if (process is null)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                var response = WorkerJson.Deserialize<TranslationWorkerResponse>(line);
                if (response is not null && _pending.TryRemove(response.Id, out var completion))
                {
                    completion.TrySetResult(response);
                }
            }

            FailPending(new InvalidOperationException("Translation worker stdout closed."));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailPending(ex);
        }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        StreamWriter? writer = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_stderrLogPath))
            {
                var directory = Path.GetDirectoryName(_stderrLogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                writer = File.AppendText(_stderrLogPath);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (writer is not null)
                {
                    await writer.WriteLineAsync(Redact(line)).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string Redact(string line)
    {
        var redacted = string.IsNullOrEmpty(_apiKey)
            ? line
            : line.Replace(_apiKey, "***", StringComparison.Ordinal);
        return BearerPattern().Replace(redacted, "Bearer ***");
    }

    private async Task StopProcessAsync()
    {
        _stopCts?.Cancel();
        var process = _process;
        if (process is not null && !process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        FailPending(new InvalidOperationException("Translation worker stopped."));
        await ObserveAsync(_stdoutTask).ConfigureAwait(false);
        await ObserveAsync(_stderrTask).ConfigureAwait(false);
        _stdoutTask = null;
        _stderrTask = null;
        _process?.Dispose();
        _process = null;
        _stopCts?.Dispose();
        _stopCts = null;
        _apiKey = "";
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var item in _pending)
        {
            if (_pending.TryRemove(item.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _stopCts?.Dispose();
    }

    [GeneratedRegex("Bearer\\s+\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();
}
