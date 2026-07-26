using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StreamTranslator.Core.Worker;

/// <summary>
/// Shared stdin/stdout JSON Lines transport for the Python worker processes.
/// Owns the process lifetime, pending-request correlation and both reader loops;
/// subclasses provide the protocol specifics: handshake, response type and wording.
/// </summary>
public abstract class JsonLinesWorkerClient<TResponse> : IAsyncDisposable
    where TResponse : class
{
    private readonly string _executablePath;
    private readonly string _arguments;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly string? _stderrLogPath;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TResponse>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _stopCts;
    private int _malformedLineCount;
    private readonly TimeSpan _requestTimeout;

    protected JsonLinesWorkerClient(
        string executablePath,
        string arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? stderrLogPath,
        TimeSpan? requestTimeout = null)
    {
        _executablePath = executablePath;
        _arguments = arguments;
        _environment = environment;
        _stderrLogPath = stderrLogPath;
        // Last-resort cap for a process that is alive but never replies; callers
        // apply their own (much shorter) per-request timeouts on top.
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>Human-readable process name used in error messages, e.g. "ASR worker".</summary>
    protected abstract string WorkerName { get; }

    protected abstract string GetResponseId(TResponse response);

    /// <summary>Applied to every stderr line before it reaches the log file.</summary>
    protected virtual string RedactStderrLine(string line)
    {
        return line;
    }

    /// <summary>Called after the process has fully stopped and per-run state was cleared.</summary>
    protected virtual void OnProcessStopped()
    {
    }

    /// <summary>Stdout lines that were not valid protocol JSON and were skipped.</summary>
    public int MalformedLineCount => Volatile.Read(ref _malformedLineCount);

    public abstract Task ShutdownAsync(CancellationToken cancellationToken = default);

    protected bool HasProcess => _process is not null;

    protected bool IsProcessRunning => _process is { HasExited: false };

    protected void StartProcess()
    {
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

        if (_environment is not null)
        {
            foreach (var item in _environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {WorkerName}.");
        _stdoutTask = Task.Run(() => ReadStdoutAsync(_stopCts.Token), CancellationToken.None);
        _stderrTask = Task.Run(() => ReadStderrAsync(_stopCts.Token), CancellationToken.None);
    }

    protected async Task<TResponse> SendAsync<TRequest>(
        string requestId,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException($"{WorkerName} is not running.");
        if (process.HasExited)
        {
            throw new InvalidOperationException($"{WorkerName} has exited.");
        }

        var completion = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException($"Duplicate {WorkerName} request id: {requestId}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        try
        {
            await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(WorkerJson.Serialize(request).AsMemory(), timeout.Token)
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }

        await using var registration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(requestId, out var pending))
            {
                pending.TrySetCanceled(timeout.Token);
            }
        });

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{WorkerName} request timed out.");
        }
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

                TResponse? response;
                try
                {
                    response = WorkerJson.Deserialize<TResponse>(line);
                }
                catch (JsonException)
                {
                    // Worker dependencies may print noise to stdout; a dirty line
                    // must not kill the reader loop for the rest of the session.
                    Interlocked.Increment(ref _malformedLineCount);
                    continue;
                }

                if (response is not null && _pending.TryRemove(GetResponseId(response), out var completion))
                {
                    completion.TrySetResult(response);
                }
            }

            FailPending(new InvalidOperationException($"{WorkerName} stdout closed."));
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
                    await writer.WriteLineAsync(RedactStderrLine(line).AsMemory(), cancellationToken).ConfigureAwait(false);
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

    protected async Task StopProcessAsync()
    {
        _stopCts?.Cancel();

        var process = _process;
        if (process is not null && !process.HasExited)
        {
            try
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        FailPending(new InvalidOperationException($"{WorkerName} stopped."));
        await ObserveReaderTaskAsync(_stdoutTask).ConfigureAwait(false);
        await ObserveReaderTaskAsync(_stderrTask).ConfigureAwait(false);
        _stdoutTask = null;
        _stderrTask = null;
        _process?.Dispose();
        _process = null;
        _stopCts?.Dispose();
        _stopCts = null;
        OnProcessStopped();
    }

    private static async Task ObserveReaderTaskAsync(Task? task)
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
        GC.SuppressFinalize(this);
    }
}
