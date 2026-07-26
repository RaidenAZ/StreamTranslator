using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace StreamTranslator.Core.Worker;

public sealed class PythonWorkerClient : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly string _arguments;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly string? _stderrLogPath;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerResponse>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _stopCts;

    public PythonWorkerClient(
        string executablePath,
        string arguments,
        IReadOnlyDictionary<string, string> environment,
        string? stderrLogPath = null)
    {
        _executablePath = executablePath;
        _arguments = arguments;
        _environment = environment;
        _stderrLogPath = stderrLogPath;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

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

        foreach (var item in _environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ASR worker.");
        _stdoutTask = Task.Run(() => ReadStdoutAsync(_stopCts.Token), CancellationToken.None);
        _stderrTask = Task.Run(() => ReadStderrAsync(_stopCts.Token), CancellationToken.None);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await SendAsync(WorkerRequest.Ping($"ping-{Guid.NewGuid():N}"), timeout.Token)
                .ConfigureAwait(false);

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

        return SendAsync(request, cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await SendAsync(WorkerRequest.Shutdown($"shutdown-{Guid.NewGuid():N}"), timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Shutdown is best-effort; the process tree is killed below if graceful exit fails.
        }

        await StopProcessAsync().ConfigureAwait(false);
    }

    private async Task<WorkerResponse> SendAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("ASR worker is not running.");
        if (process.HasExited)
        {
            throw new InvalidOperationException("ASR worker has exited.");
        }

        var completion = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, completion))
        {
            throw new InvalidOperationException($"Duplicate worker request id: {request.Id}");
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(WorkerJson.Serialize(request).AsMemory(), cancellationToken)
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

                var response = WorkerJson.Deserialize<WorkerResponse>(line);
                if (response is not null && _pending.TryRemove(response.Id, out var completion))
                {
                    completion.TrySetResult(response);
                }
            }

            FailPending(new InvalidOperationException("ASR worker stdout closed."));
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
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
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

    private async Task StopProcessAsync()
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

        FailPending(new InvalidOperationException("ASR worker stopped."));
        await ObserveReaderTaskAsync(_stdoutTask).ConfigureAwait(false);
        await ObserveReaderTaskAsync(_stderrTask).ConfigureAwait(false);
        _stdoutTask = null;
        _stderrTask = null;
        _process?.Dispose();
        _process = null;
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
    }
}
