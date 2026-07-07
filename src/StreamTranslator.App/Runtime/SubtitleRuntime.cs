using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using StreamTranslator.Audio.Capture;
using StreamTranslator.Audio.Encoding;
using StreamTranslator.Audio.Segmentation;
using StreamTranslator.Audio.Vad;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Subtitles;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.App.Runtime;

public sealed class SubtitleRuntime : IAsyncDisposable
{
    private const string DefaultMimoBaseUrl = "https://api.xiaomimimo.com/v1";
    private readonly string _baseDirectory;
    private readonly string _dataDirectory;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _asrSemaphore;
    private LoopbackCaptureService? _capture;
    private IVadEngine? _vad;
    private SpeechSegmenter? _segmenter;
    private PythonWorkerClient? _worker;
    private SubtitleHistoryStore? _history;
    private readonly SubtitleReorderBuffer _reorderBuffer = new(firstSequence: 1);
    private readonly object _subtitleLock = new();
    private readonly object _tasksLock = new();
    private readonly List<Task> _segmentTasks = [];
    private readonly List<DiagnosticSegmentRecord> _diagnosticSegments = [];
    private long _sequence;
    private string _lastFinalText = "";
    private StreamWriter? _vadTimelineWriter;
    private string? _diagnosticSessionPath;
    private CancellationTokenSource? _stopCts;
    private bool _stopping;

    public SubtitleRuntime(string baseDirectory, string dataDirectory, AppSettings settings)
    {
        _baseDirectory = baseDirectory;
        _dataDirectory = dataDirectory;
        _settings = settings;
        _asrSemaphore = new SemaphoreSlim(Math.Max(1, settings.Asr.MaxConcurrency), Math.Max(1, settings.Asr.MaxConcurrency));
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<SubtitleItem>? SubtitleReady;
    public event EventHandler<Exception>? RuntimeError;
    public event EventHandler<double>? AudioLevelChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _stopping = false;
        _stopCts = new CancellationTokenSource();
        _history = new SubtitleHistoryStore(Path.Combine(_dataDirectory, "subtitles"));
        StartDiagnosticsSession();
        _vad = CreateVadEngine();
        _segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = _settings.Vad.EndSilenceMs,
            StartSpeechMs = _settings.Vad.StartSpeechMs,
            MinSegmentMs = _settings.Vad.MinSegmentMs,
            SoftMaxSegmentMs = _settings.Vad.SoftMaxSegmentMs,
            HardMaxSegmentMs = _settings.Vad.HardMaxSegmentMs,
            OverlapMs = _settings.Vad.OverlapMs
        });

        _worker = CreateWorkerClient();
        await _worker.StartAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "ASR worker 已启动");

        _capture = new LoopbackCaptureService(
            new AudioDeviceService(),
            _settings.Audio.DeviceId,
            _settings.Audio.FollowDefaultDevice);
        _capture.FrameCaptured += OnFrameCaptured;
        _capture.Start();
        StatusChanged?.Invoke(this, "音频捕获已启动");
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _stopCts?.Cancel();

        if (_capture is not null)
        {
            _capture.FrameCaptured -= OnFrameCaptured;
            _capture.Dispose();
            _capture = null;
        }

        await WaitForSegmentTasksAsync().ConfigureAwait(false);

        _vad?.Dispose();
        _vad = null;
        _segmenter = null;
        await StopDiagnosticsSessionAsync().ConfigureAwait(false);

        if (_worker is not null)
        {
            await _worker.ShutdownAsync().ConfigureAwait(false);
            await _worker.DisposeAsync().ConfigureAwait(false);
            _worker = null;
        }

        StatusChanged?.Invoke(this, "已停止");
        _stopCts?.Dispose();
        _stopCts = null;
    }

    private void OnFrameCaptured(object? sender, PcmAudioFrame frame)
    {
        try
        {
            if (_stopping)
            {
                return;
            }

            var vad = _vad;
            var segmenter = _segmenter;
            if (vad is null || segmenter is null)
            {
                return;
            }

            var decision = vad.Analyze(frame.Samples, frame.SampleRate);
            AudioLevelChanged?.Invoke(this, CalculateLevel(frame.Samples));
            WriteVadDiagnostic(frame, decision);
            var completed = segmenter.Push(frame, decision);
            if (completed is not null)
            {
                var task = ProcessSegmentAsync(completed, _stopCts?.Token ?? CancellationToken.None);
                TrackSegmentTask(task);
            }
        }
        catch (Exception ex)
        {
            RuntimeError?.Invoke(this, ex);
        }
    }

    private void TrackSegmentTask(Task task)
    {
        lock (_tasksLock)
        {
            _segmentTasks.Add(task);
        }

        _ = task.ContinueWith(completedTask =>
        {
            lock (_tasksLock)
            {
                _segmentTasks.Remove(completedTask);
            }
        }, TaskScheduler.Default);
    }

    private async Task WaitForSegmentTasksAsync()
    {
        Task[] tasks;
        lock (_tasksLock)
        {
            tasks = _segmentTasks.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessSegmentAsync(CompletedSpeechSegment segment, CancellationToken cancellationToken)
    {
        var worker = _worker;
        if (worker is null)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        var acquired = false;
        try
        {
            await _asrSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            var wav = WavEncoder.EncodePcm16Mono(segment.Samples, segment.SampleRate);
            var diagnosticRecord = SaveSegmentDiagnostic(sequence, segment, wav);
            var request = WorkerRequest.Transcribe(
                id: $"seg-{sequence:000000}",
                sequence: sequence,
                startMs: segment.StartMs,
                endMs: segment.EndMs,
                sampleRate: segment.SampleRate,
                language: _settings.Asr.Language,
                audioBase64: Convert.ToBase64String(wav));

            var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
            UpdateDiagnosticAsr(diagnosticRecord, response);
            StatusChanged?.Invoke(
                this,
                response.Ok
                    ? $"ASR API 正常 ({response.LatencyMs?.ToString() ?? "-"} ms)"
                    : $"ASR API 错误: {response.ErrorCode ?? "Unknown"}");

            var subtitle = new SubtitleItem
            {
                Sequence = sequence,
                Start = TimeSpan.FromMilliseconds(segment.StartMs),
                End = TimeSpan.FromMilliseconds(segment.EndMs),
                SourceText = response.Ok ? response.Text ?? "" : $"识别失败: {response.ErrorMessage}",
                Status = response.Ok ? SubtitleStatus.Final : SubtitleStatus.Failed
            };

            SubtitleItem[] displayItems;
            lock (_subtitleLock)
            {
                var releasedItems = _reorderBuffer.Add(subtitle);
                displayItems = new SubtitleItem[releasedItems.Count];
                for (var i = 0; i < releasedItems.Count; i++)
                {
                    var deduplicated = ApplyDeduplicationLocked(releasedItems[i]);
                    UpdateDiagnosticDedupLocked(deduplicated.Item.Sequence, deduplicated.DedupApplied);
                    displayItems[i] = deduplicated.Item;
                }
            }

            foreach (var displayItem in displayItems)
            {
                if (_history is not null)
                {
                    await _history.AppendAsync(displayItem, DateOnly.FromDateTime(DateTime.Now)).ConfigureAwait(false);
                }

                SubtitleReady?.Invoke(this, displayItem);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RuntimeError?.Invoke(this, ex);
        }
        finally
        {
            if (acquired)
            {
                _asrSemaphore.Release();
            }
        }
    }

    private IVadEngine CreateVadEngine()
    {
        var modelPath = Path.Combine(_baseDirectory, "models", "silero_vad.onnx");
        if (File.Exists(modelPath))
        {
            StatusChanged?.Invoke(this, "Silero ONNX VAD 已加载");
            return new SileroOnnxVadEngine(modelPath);
        }

        StatusChanged?.Invoke(this, "VAD 模型缺失，使用 Energy fallback");
        return new EnergyVadEngine();
    }

    private async Task<WorkerResponse> SendWithRetryAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Ok || string.Equals(response.ErrorCode, "ValueError", StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeError?.Invoke(this, ex);
            await RestartWorkerAsync(cancellationToken).ConfigureAwait(false);
        }

        return await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerResponse> SendOnceAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var worker = _worker ?? throw new InvalidOperationException("ASR worker is not running.");
        return await worker.TranscribeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null)
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
        }

        _worker = CreateWorkerClient();
        await _worker.StartAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "ASR worker 已重启");
    }

    private PythonWorkerClient CreateWorkerClient()
    {
        var workerExe = Path.Combine(_baseDirectory, "worker", "asr_worker.exe");
        var hasWorkerExe = File.Exists(workerExe);
        var workerScript = hasWorkerExe ? "" : FindWorkerScriptPath();
        var pythonExecutable = Environment.GetEnvironmentVariable("STREAMTRANSLATOR_PYTHON");
        var executable = hasWorkerExe
            ? workerExe
            : string.IsNullOrWhiteSpace(pythonExecutable) ? "python" : pythonExecutable;
        var arguments = hasWorkerExe ? "" : $"\"{workerScript}\"";
        var timeoutSeconds = Math.Max(1, _settings.Asr.TimeoutMs / 1000);

        var environment = new Dictionary<string, string>
        {
            ["MIMO_API_KEY"] = _settings.Asr.ApiKey,
            ["MIMO_BASE_URL"] = string.IsNullOrWhiteSpace(_settings.Asr.BaseUrl) ? DefaultMimoBaseUrl : _settings.Asr.BaseUrl,
            ["MIMO_ASR_MODEL"] = _settings.Asr.Model,
            ["MIMO_TIMEOUT_SECONDS"] = timeoutSeconds.ToString()
        };

        return new PythonWorkerClient(
            executable,
            arguments,
            environment,
            Path.Combine(_dataDirectory, "logs", "worker.log"));
    }

    private string FindWorkerScriptPath()
    {
        var directory = new DirectoryInfo(_baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "python", "asr_worker.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("ASR worker executable and python/asr_worker.py were not found.");
    }

    private static double CalculateLevel(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        var sum = 0d;
        foreach (var sample in samples)
        {
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        var rms = Math.Sqrt(sum / samples.Length);
        return Math.Clamp(rms * 160, 0, 100);
    }

    private (SubtitleItem Item, bool DedupApplied) ApplyDeduplicationLocked(SubtitleItem item)
    {
        if (item.Status != SubtitleStatus.Final || string.IsNullOrWhiteSpace(item.SourceText))
        {
            return (item, false);
        }

        if (string.IsNullOrWhiteSpace(_lastFinalText))
        {
            _lastFinalText = item.SourceText;
            return (item, false);
        }

        var merged = TextDeduplicator.MergeOverlap(_lastFinalText, item.SourceText);
        _lastFinalText = merged.Deduplicated ? merged.MergedText : item.SourceText;
        return merged.Deduplicated ? (item with { SourceText = merged.AppendedText }, true) : (item, false);
    }

    private void StartDiagnosticsSession()
    {
        if (!_settings.Diagnostics.Enabled)
        {
            return;
        }

        var sessionId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var root = Path.Combine(_dataDirectory, "debug-audio");
        Directory.CreateDirectory(Path.Combine(root, "segments"));
        Directory.CreateDirectory(Path.Combine(root, "vad"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        _vadTimelineWriter = File.CreateText(Path.Combine(root, "vad", $"session-{sessionId}.vad.jsonl"));
        _diagnosticSessionPath = Path.Combine(root, "sessions", $"session-{sessionId}.json");
    }

    private void WriteVadDiagnostic(PcmAudioFrame frame, VadDecision decision)
    {
        if (_vadTimelineWriter is null || !_settings.Diagnostics.SaveVadTimeline)
        {
            return;
        }

        var record = JsonSerializer.Serialize(new
        {
            timeMs = frame.StartMs,
            probability = decision.Probability,
            isSpeech = decision.IsSpeech
        }, DiagnosticJsonOptions);
        _vadTimelineWriter.WriteLine(record);
    }

    private DiagnosticSegmentRecord? SaveSegmentDiagnostic(long sequence, CompletedSpeechSegment segment, byte[] wav)
    {
        if (!_settings.Diagnostics.Enabled)
        {
            return null;
        }

        string? fileName = null;
        if (_settings.Diagnostics.SaveSegmentAudio)
        {
            fileName = $"seg-{sequence:000000}.wav";
            var path = Path.Combine(_dataDirectory, "debug-audio", "segments", fileName);
            File.WriteAllBytes(path, wav);
        }

        var record = new DiagnosticSegmentRecord
        {
            Sequence = sequence,
            StartMs = segment.StartMs,
            EndMs = segment.EndMs,
            DurationMs = segment.EndMs - segment.StartMs,
            CutReason = segment.CutReason.ToString(),
            OverlapMs = segment.OverlapMs,
            File = fileName
        };

        lock (_subtitleLock)
        {
            _diagnosticSegments.Add(record);
        }

        return record;
    }

    private static void UpdateDiagnosticAsr(DiagnosticSegmentRecord? record, WorkerResponse response)
    {
        if (record is null)
        {
            return;
        }

        record.AsrLatencyMs = response.LatencyMs;
        record.AsrOk = response.Ok;
        record.AsrErrorCode = response.ErrorCode;
    }

    private void UpdateDiagnosticDedupLocked(long sequence, bool dedupApplied)
    {
        if (!_settings.Diagnostics.Enabled)
        {
            return;
        }

        var record = _diagnosticSegments.FirstOrDefault(item => item.Sequence == sequence);
        if (record is not null)
        {
            record.DedupApplied = dedupApplied;
        }
    }

    private async Task StopDiagnosticsSessionAsync()
    {
        if (_vadTimelineWriter is not null)
        {
            await _vadTimelineWriter.DisposeAsync().ConfigureAwait(false);
            _vadTimelineWriter = null;
        }

        if (!string.IsNullOrWhiteSpace(_diagnosticSessionPath))
        {
            var session = new
            {
                createdAt = DateTimeOffset.Now,
                vad = _settings.Vad,
                asr = new
                {
                    _settings.Asr.BaseUrl,
                    _settings.Asr.Model,
                    _settings.Asr.Language,
                    _settings.Asr.TimeoutMs,
                    _settings.Asr.MaxConcurrency
                },
                segmentCount = _diagnosticSegments.Count,
                segments = _diagnosticSegments
            };
            await File.WriteAllTextAsync(_diagnosticSessionPath, JsonSerializer.Serialize(session, DiagnosticJsonOptions)).ConfigureAwait(false);
            _diagnosticSessionPath = null;
        }
    }

    private sealed class DiagnosticSegmentRecord
    {
        public long Sequence { get; init; }
        public long StartMs { get; init; }
        public long EndMs { get; init; }
        public long DurationMs { get; init; }
        public string CutReason { get; init; } = "";
        public int OverlapMs { get; init; }
        public string? File { get; init; }
        public int? AsrLatencyMs { get; set; }
        public bool? AsrOk { get; set; }
        public string? AsrErrorCode { get; set; }
        public bool? DedupApplied { get; set; }
    }

    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _asrSemaphore.Dispose();
    }
}
