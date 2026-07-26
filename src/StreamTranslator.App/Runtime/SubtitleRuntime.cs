using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using StreamTranslator.Audio.Capture;
using StreamTranslator.Audio.Encoding;
using StreamTranslator.Audio.Segmentation;
using StreamTranslator.Audio.Vad;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Subtitles;
using StreamTranslator.Core.Translation;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.App.Runtime;

public sealed class SubtitleRuntime : IAsyncDisposable
{
    private const string DefaultMimoBaseUrl = "https://api.xiaomimimo.com/v1";
    private readonly string _baseDirectory;
    private readonly string _dataDirectory;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _asrSemaphore;
    private readonly SemaphoreSlim _workerRestartLock = new(1, 1);
    private readonly SemaphoreSlim _captureRecoveryLock = new(1, 1);
    private readonly SemaphoreSlim _subtitlePublishLock = new(1, 1);
    private LoopbackCaptureService? _capture;
    private IVadEngine? _vad;
    private AdaptiveEndpointController? _endpointController;
    private SpeechSegmenter? _segmenter;
    private PythonWorkerClient? _worker;
    private TranslationSession? _translationSession;
    private SubtitleHistoryStore? _history;
    private readonly SubtitleReorderBuffer _reorderBuffer = new(firstSequence: 1);
    private readonly UtteranceGroupTracker _utteranceGroupTracker;
    private readonly SubtitleRevisionCoordinator _revisionCoordinator = new();
    private readonly object _subtitleLock = new();
    private readonly object _tasksLock = new();
    private readonly object _captureTaskLock = new();
    private readonly List<Task> _segmentTasks = [];
    private readonly List<DiagnosticSegmentRecord> _diagnosticSegments = [];
    private long _sequence;
    private string _lastFinalText = "";
    private StreamWriter? _vadTimelineWriter;
    private StreamWriter? _translationDiagnosticWriter;
    private readonly object _translationDiagnosticLock = new();
    private string? _diagnosticSessionPath;
    private CancellationTokenSource? _stopCts;
    private Task? _captureRecoveryTask;
    private Task? _stopTask;
    private Channel<string>? _adaptiveMetricsChannel;
    private Task? _adaptiveMetricsWriterTask;
    private bool _stopping;
    private bool _mergeNextSegment;
    private int _workerRestartCount;

    public SubtitleRuntime(string baseDirectory, string dataDirectory, AppSettings settings)
    {
        _baseDirectory = baseDirectory;
        _dataDirectory = dataDirectory;
        _settings = settings with
        {
            Asr = settings.Asr with { Language = "auto" }
        };
        _utteranceGroupTracker = new UtteranceGroupTracker($"utt-{Guid.NewGuid():N}");
        _asrSemaphore = new SemaphoreSlim(Math.Max(1, settings.Asr.MaxConcurrency), Math.Max(1, settings.Asr.MaxConcurrency));
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<SubtitleItem>? SubtitleReady;
    public event EventHandler<Exception>? RuntimeError;
    public event EventHandler<double>? AudioLevelChanged;
    public event EventHandler<VadEndpointRuntimeStatus>? VadEndpointChanged;
    public event EventHandler<TranslationResultUpdate>? TranslationReady;
    public event EventHandler<TranslationRuntimeStatus>? TranslationStatusChanged;
    public event EventHandler<TranslationTaskStatusUpdate>? TranslationTaskStatusChanged;

    public void UpdateTranslationVisibleGroups(IReadOnlyCollection<string> visibleGroupIds)
    {
        _translationSession?.UpdateVisibleGroups(visibleGroupIds);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _stopping = false;
        _workerRestartCount = 0;
        _stopCts = new CancellationTokenSource();
        _history = new SubtitleHistoryStore(Path.Combine(_dataDirectory, "subtitles"));
        StartAdaptiveMetricsWriter();
        StartDiagnosticsSession();
        _vad = CreateVadEngine();
        _endpointController = CreateEndpointController();
        _segmenter = CreateSpeechSegmenter();

        _worker = CreateWorkerClient();
        await _worker.StartAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "ASR worker 已启动");

        await StartTranslationSessionAsync(cancellationToken).ConfigureAwait(false);

        _capture = new LoopbackCaptureService(
            new AudioDeviceService(),
            _settings.Audio.DeviceId,
            _settings.Audio.FollowDefaultDevice);
        _capture.FrameCaptured += OnFrameCaptured;
        _capture.CaptureStopped += OnCaptureStopped;
        _capture.Start();
        StatusChanged?.Invoke(this, "音频捕获已启动");
    }

    public Task StopAsync()
    {
        // Hotkey reentry or DisposeAsync after StopAsync must not run the
        // teardown twice; every caller awaits the same one-shot task. All
        // callers are on the UI thread, so the non-atomic ??= is safe.
        return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        _stopCts?.Cancel();

        if (_capture is not null)
        {
            _capture.FrameCaptured -= OnFrameCaptured;
            _capture.CaptureStopped -= OnCaptureStopped;
            _capture.Dispose();
            _capture = null;
        }

        await WaitForCaptureRecoveryAsync().ConfigureAwait(false);

        await WaitForSegmentTasksAsync().ConfigureAwait(false);

        if (_translationSession is not null)
        {
            _translationSession.TranslationReady -= OnTranslationReady;
            _translationSession.StatusChanged -= OnTranslationStatusChanged;
            _translationSession.TaskStatusChanged -= OnTranslationTaskStatusChanged;
            await _translationSession.DisposeAsync().ConfigureAwait(false);
            _translationSession.DiagnosticEvent -= OnTranslationDiagnosticEvent;
            WriteTranslationMetrics(_translationSession);
            _translationSession = null;
        }

        _vad?.Dispose();
        _vad = null;
        if (_endpointController is not null)
        {
            _endpointController.EndpointAdjusted -= OnEndpointAdjusted;
            _endpointController = null;
        }
        _segmenter = null;
        await StopDiagnosticsSessionAsync().ConfigureAwait(false);
        await StopAdaptiveMetricsWriterAsync().ConfigureAwait(false);

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
            var endpointController = _endpointController;
            var segmenter = _segmenter;
            if (vad is null || endpointController is null || segmenter is null)
            {
                return;
            }

            var decision = vad.Analyze(frame.Samples, frame.SampleRate);
            AudioLevelChanged?.Invoke(this, CalculateLevel(frame.Samples));
            WriteVadDiagnostic(frame, decision);
            var endpointObservation = endpointController.ObserveVad(frame.StartMs, frame.DurationMs, decision.IsSpeech);
            if (endpointObservation.Evaluation is { } evaluation)
            {
                WriteAdaptiveMetric(new
                {
                    type = "endpoint_evaluation",
                    timestamp = DateTimeOffset.Now,
                    timelineMs = evaluation.TimestampMs,
                    mode = endpointController.Mode.ToString(),
                    signal = evaluation.Signal.ToString(),
                    decision = evaluation.Decision.ToString(),
                    effectiveEndSilenceMs = evaluation.EffectiveEndSilenceMs,
                    minimumEndSilenceMs = endpointController.MinimumEndSilenceMs,
                    maximumEndSilenceMs = endpointController.MaximumEndSilenceMs,
                    sampleCount = evaluation.SampleCount,
                    p75PauseMs = evaluation.P75PauseMs,
                    targetEndSilenceMs = evaluation.TargetEndSilenceMs,
                    consecutiveQuickResumes = evaluation.ConsecutiveQuickResumes,
                    recentAdjustmentCount = evaluation.RecentAdjustmentCount,
                    cooldownRemainingMs = evaluation.CooldownRemainingMs
                });
                PublishVadEndpointStatus(endpointController, endpointObservation.Adjustment, evaluation);
            }

            if (endpointObservation.QuickResume is { } quickResume)
            {
                if (quickResume.ShouldMergeWithPreviousSegment)
                {
                    _mergeNextSegment = true;
                }

                WriteAdaptiveMetric(new
                {
                    type = "quick_resume",
                    timestamp = DateTimeOffset.Now,
                    timelineMs = frame.StartMs,
                    quickResume.CompletePauseMs,
                    quickResume.ShouldMergeWithPreviousSegment
                });
            }

            var completed = segmenter.Push(frame, decision, endpointController.EffectiveEndSilenceMs);
            if (completed is not null)
            {
                endpointController.NotifySegmentCut(completed.EndMs, completed.CutReason);
                var sequence = Interlocked.Increment(ref _sequence);
                var groupAssignment = _utteranceGroupTracker.Assign(
                    sequence,
                    completed.StartMs,
                    completed.EndMs,
                    _mergeNextSegment);
                _mergeNextSegment = false;
                WriteAdaptiveMetric(new
                {
                    type = "utterance_group_assignment",
                    timestamp = DateTimeOffset.Now,
                    sequence,
                    groupAssignment.UtteranceGroupId,
                    groupAssignment.SegmentCount,
                    groupAssignment.IsContinuation,
                    groupSpanMs = groupAssignment.GroupEndMs - groupAssignment.GroupStartMs
                });
                var task = ProcessSegmentAsync(
                    sequence,
                    completed,
                    groupAssignment,
                    _stopCts?.Token ?? CancellationToken.None);
                TrackSegmentTask(task);
            }
        }
        catch (Exception ex)
        {
            RuntimeError?.Invoke(this, ex);
        }
    }

    private void OnCaptureStopped(object? sender, AudioCaptureStoppedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        lock (_captureTaskLock)
        {
            if (_captureRecoveryTask is { IsCompleted: false })
            {
                return;
            }

            _captureRecoveryTask = HandleCaptureStoppedAsync(e);
        }
    }

    private async Task HandleCaptureStoppedAsync(AudioCaptureStoppedEventArgs e)
    {
        await _captureRecoveryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping)
            {
                return;
            }

            if (_settings.Audio.FollowDefaultDevice && _capture is not null)
            {
                StatusChanged?.Invoke(this, "音频捕获中断，正在切换默认设备");
                try
                {
                    await Task.Delay(500, _stopCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                    _capture.Start();
                    _vad?.Reset();
                    if (_endpointController is not null)
                    {
                        _endpointController.EndpointAdjusted -= OnEndpointAdjusted;
                    }
                    _endpointController = CreateEndpointController();
                    _segmenter = CreateSpeechSegmenter();
                    _utteranceGroupTracker.CloseCurrentGroup();
                    _revisionCoordinator.CloseCurrentGroup();
                    _mergeNextSegment = false;
                    StatusChanged?.Invoke(this, "音频捕获已恢复");
                    return;
                }
                catch (OperationCanceledException) when (_stopping)
                {
                    return;
                }
                catch (Exception restartException)
                {
                    var fatal = new RuntimeFatalException("默认音频设备切换失败，请检查系统输出设备。", restartException);
                    RuntimeError?.Invoke(this, fatal);
                    return;
                }
            }

            RuntimeError?.Invoke(
                this,
                new RuntimeFatalException("所选音频设备已断开，请重新选择输出设备。", e.Exception));
        }
        finally
        {
            _captureRecoveryLock.Release();
        }
    }

    private async Task WaitForCaptureRecoveryAsync()
    {
        Task? task;
        lock (_captureTaskLock)
        {
            task = _captureRecoveryTask;
        }

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
        catch (Exception)
        {
            // A faulted segment task must not abort the stop sequence; the
            // failure was already surfaced through RuntimeError when it happened.
        }
    }

    private async Task ProcessSegmentAsync(
        long sequence,
        CompletedSpeechSegment segment,
        UtteranceGroupAssignment groupAssignment,
        CancellationToken cancellationToken)
    {
        var requestId = $"seg-{sequence:000000}";
        var acquired = false;
        DiagnosticSegmentRecord? diagnosticRecord = null;
        WorkerResponse response;
        RuntimeFatalException? fatalError = null;
        try
        {
            await _asrSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            var wav = WavEncoder.EncodePcm16Mono(segment.Samples, segment.SampleRate);
            diagnosticRecord = SaveSegmentDiagnostic(sequence, segment, groupAssignment, wav);
            WriteAdaptiveMetric(new
            {
                type = "asr_request",
                timestamp = DateTimeOffset.Now,
                sequence,
                groupAssignment.UtteranceGroupId
            });
            var request = WorkerRequest.Transcribe(
                id: requestId,
                sequence: sequence,
                startMs: segment.StartMs,
                endMs: segment.EndMs,
                sampleRate: segment.SampleRate,
                language: "auto",
                audioBase64: Convert.ToBase64String(wav));

            response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Ok && string.IsNullOrWhiteSpace(response.Text))
            {
                response = response with
                {
                    Ok = false,
                    ErrorCode = "EmptyResult",
                    ErrorMessage = "ASR 未返回文本",
                    ErrorKind = "empty_result",
                    Retryable = false
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            fatalError = ex as RuntimeFatalException;
            response = CreateFailureResponse(requestId, sequence, ex);
        }
        finally
        {
            if (acquired)
            {
                _asrSemaphore.Release();
            }
        }

        UpdateDiagnosticAsr(diagnosticRecord, response);
        StatusChanged?.Invoke(
            this,
            response.Ok
                ? $"ASR API 正常 ({response.LatencyMs?.ToString() ?? "-"} ms)"
                : $"ASR API 错误: {response.ErrorKind ?? response.ErrorCode ?? "Unknown"}");

        try
        {
            await PublishTerminalResponseAsync(sequence, segment, groupAssignment, response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A publish/history failure must not leave this segment task faulted:
            // StopAsync awaits these tasks and an escaped exception would take the
            // whole app down through the async-void stop path.
            RuntimeError?.Invoke(this, new InvalidOperationException($"字幕发布失败: {ex.Message}", ex));
        }

        if (fatalError is not null || WorkerFailurePolicy.Decide(response, attempt: 1) == WorkerFailureAction.StopRuntime)
        {
            RuntimeError?.Invoke(
                this,
                fatalError ?? new RuntimeFatalException("MiMo API 鉴权失败，请检查 API Key 和访问权限。"));
        }
    }

    private async Task PublishTerminalResponseAsync(
        long sequence,
        CompletedSpeechSegment segment,
        UtteranceGroupAssignment groupAssignment,
        WorkerResponse response)
    {
        await _subtitlePublishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var hasText = response.Ok && !string.IsNullOrWhiteSpace(response.Text);
            var subtitle = new SubtitleItem
            {
                Sequence = sequence,
                UtteranceGroupId = groupAssignment.UtteranceGroupId,
                Start = TimeSpan.FromMilliseconds(segment.StartMs),
                End = TimeSpan.FromMilliseconds(segment.EndMs),
                SourceText = hasText ? response.Text! : $"识别失败: {response.ErrorMessage ?? "ASR 未返回文本"}",
                Status = hasText ? SubtitleStatus.Final : SubtitleStatus.Failed
            };

            (SubtitleItem HistoryItem, SubtitlePublication Publication)[] publications;
            lock (_subtitleLock)
            {
                var releasedItems = _reorderBuffer.Add(subtitle);
                publications = new (SubtitleItem, SubtitlePublication)[releasedItems.Count];
                for (var index = 0; index < releasedItems.Count; index++)
                {
                    var deduplicated = ApplyDeduplicationLocked(releasedItems[index]);
                    UpdateDiagnosticDedupLocked(deduplicated.Item.Sequence, deduplicated.DedupApplied);
                    var historyItem = deduplicated.Item with
                    {
                        Type = "subtitle",
                        Revision = 1,
                        ReplacesSequences = [deduplicated.Item.Sequence],
                        GeneratedAt = deduplicated.Item.GeneratedAt ?? DateTimeOffset.Now
                    };
                    publications[index] = (historyItem, _revisionCoordinator.Publish(historyItem));
                }
            }

            foreach (var (historyItem, publication) in publications)
            {
                var generatedAt = historyItem.GeneratedAt ?? DateTimeOffset.Now;

                if (_history is not null)
                {
                    try
                    {
                        var historyDate = DateOnly.FromDateTime(generatedAt.Date);
                        await _history.AppendAsync(historyItem, historyDate).ConfigureAwait(false);
                        if (publication.Kind == SubtitlePublicationKind.Revise)
                        {
                            await _history.AppendRevisionAsync(publication.Item, historyDate).ConfigureAwait(false);
                        }
                    }
                    catch (Exception historyException)
                    {
                        RuntimeError?.Invoke(this, historyException);
                    }
                }

                if (publication.Kind == SubtitlePublicationKind.Revise)
                {
                    WriteAdaptiveMetric(new
                    {
                        type = "subtitle_revision",
                        timestamp = DateTimeOffset.Now,
                        publication.Item.UtteranceGroupId,
                        publication.Item.Revision,
                        publication.Item.ReplacesSequences
                    });
                }

                SubtitleReady?.Invoke(this, publication.Item);
                _translationSession?.Submit(publication.Item);
            }
        }
        finally
        {
            _subtitlePublishLock.Release();
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

        throw new FileNotFoundException("Silero VAD 模型缺失，无法以准确模式启动字幕。", modelPath);
    }

    private SpeechSegmenter CreateSpeechSegmenter()
    {
        return new SpeechSegmenter(new SpeechSegmenterOptions
        {
            EndSilenceMs = _endpointController?.EffectiveEndSilenceMs ?? _settings.Vad.EndSilenceMs,
            StartSpeechMs = _settings.Vad.StartSpeechMs,
            PreRollMs = _settings.Vad.PreRollMs,
            MinSegmentMs = _settings.Vad.MinSegmentMs,
            SoftBreakSilenceMs = _settings.Vad.SoftBreakSilenceMs,
            SoftMaxSegmentMs = _settings.Vad.SoftMaxSegmentMs,
            HardMaxSegmentMs = _settings.Vad.HardMaxSegmentMs,
            OverlapMs = _settings.Vad.OverlapMs
        });
    }

    private AdaptiveEndpointController CreateEndpointController()
    {
        var controller = new AdaptiveEndpointController(
            _settings.Vad.EndpointMode,
            _settings.Vad.EndSilenceMs,
            _settings.Vad.StartSpeechMs);
        controller.EndpointAdjusted += OnEndpointAdjusted;
        PublishVadEndpointStatus(controller, adjustment: null, evaluation: null);
        WriteAdaptiveMetric(new
        {
            type = "session_start",
            timestamp = DateTimeOffset.Now,
            mode = controller.Mode.ToString(),
            effectiveEndSilenceMs = controller.EffectiveEndSilenceMs,
            minimumEndSilenceMs = controller.MinimumEndSilenceMs,
            maximumEndSilenceMs = controller.MaximumEndSilenceMs,
            minSegmentMs = _settings.Vad.MinSegmentMs,
            softMaxSegmentMs = _settings.Vad.SoftMaxSegmentMs,
            hardMaxSegmentMs = _settings.Vad.HardMaxSegmentMs,
            asrModel = _settings.Asr.Model,
            asrLanguage = "auto"
        });
        return controller;
    }

    private void OnEndpointAdjusted(object? sender, EndpointAdjustment adjustment)
    {
        if (sender is not AdaptiveEndpointController controller)
        {
            return;
        }

        WriteAdaptiveMetric(new
        {
            type = "endpoint_adjustment",
            timestamp = DateTimeOffset.Now,
            timelineMs = adjustment.TimestampMs,
            mode = controller.Mode.ToString(),
            previousEndSilenceMs = adjustment.PreviousEndSilenceMs,
            currentEndSilenceMs = adjustment.CurrentEndSilenceMs,
            reason = adjustment.Reason.ToString(),
            sampleCount = adjustment.SampleCount,
            p75PauseMs = adjustment.P75PauseMs,
            targetEndSilenceMs = adjustment.TargetEndSilenceMs
        });
    }

    private void PublishVadEndpointStatus(
        AdaptiveEndpointController controller,
        EndpointAdjustment? adjustment,
        EndpointEvaluation? evaluation)
    {
        VadEndpointChanged?.Invoke(
            this,
            new VadEndpointRuntimeStatus(
                controller.Mode,
                controller.EffectiveEndSilenceMs,
                controller.IsAdaptive,
                adjustment,
                evaluation));
    }

    private void WriteAdaptiveMetric(object metric)
    {
        try
        {
            var line = JsonSerializer.Serialize(metric, DiagnosticJsonOptions);
            _adaptiveMetricsChannel?.Writer.TryWrite(line);
        }
        catch
        {
            // Metrics must not interrupt audio capture.
        }
    }

    private void StartAdaptiveMetricsWriter()
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _adaptiveMetricsChannel = channel;
        _adaptiveMetricsWriterTask = Task.Run(async () =>
        {
            try
            {
                var logsDirectory = Path.Combine(_dataDirectory, "logs");
                Directory.CreateDirectory(logsDirectory);
                var path = Path.Combine(logsDirectory, "adaptive-vad.jsonl");
                await using var writer = new StreamWriter(path, append: true);
                await foreach (var line in channel.Reader.ReadAllAsync())
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // Metrics must not interrupt audio capture or shutdown.
            }
        });
    }

    private async Task StopAdaptiveMetricsWriterAsync()
    {
        var channel = _adaptiveMetricsChannel;
        var writerTask = _adaptiveMetricsWriterTask;
        _adaptiveMetricsChannel = null;
        _adaptiveMetricsWriterTask = null;
        channel?.Writer.TryComplete();
        if (writerTask is not null)
        {
            await writerTask.ConfigureAwait(false);
        }
    }

    private async Task<WorkerResponse> SendWithRetryAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var worker = _worker ?? throw new RuntimeFatalException("ASR worker 未运行。");
            try
            {
                var response = await SendOnceAsync(worker, request, cancellationToken).ConfigureAwait(false);
                var action = WorkerFailurePolicy.Decide(response, attempt);
                if (action != WorkerFailureAction.Retry)
                {
                    return response;
                }

                if (string.Equals(response.ErrorKind, "rate_limit", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt == 0)
            {
                StatusChanged?.Invoke(this, $"ASR worker 异常，正在重启: {ex.Message}");
                await EnsureWorkerRestartedAsync(worker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new RuntimeFatalException("ASR worker 重启后仍无法完成请求，字幕已停止。", ex);
            }
        }

        return CreateFailureResponse(request.Id, request.Sequence, new TimeoutException("ASR request retry exhausted."));
    }

    private async Task<WorkerResponse> SendOnceAsync(
        PythonWorkerClient worker,
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_settings.Asr.TimeoutMs));
        try
        {
            return await worker.TranscribeAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ASR request exceeded {_settings.Asr.TimeoutMs} ms.");
        }
    }

    private async Task EnsureWorkerRestartedAsync(
        PythonWorkerClient failedWorker,
        CancellationToken cancellationToken)
    {
        await _workerRestartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_worker, failedWorker))
            {
                return;
            }

            if (_workerRestartCount >= 1)
            {
                throw new RuntimeFatalException("ASR worker 已重启过一次，仍然异常，字幕已停止。");
            }

            _workerRestartCount++;
            await failedWorker.DisposeAsync().ConfigureAwait(false);
            var replacement = CreateWorkerClient();
            try
            {
                await replacement.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                throw new RuntimeFatalException("ASR worker 重启失败，字幕已停止。", ex);
            }

            _worker = replacement;
            StatusChanged?.Invoke(this, "ASR worker 已重启");
        }
        finally
        {
            _workerRestartLock.Release();
        }
    }

    private static WorkerResponse CreateFailureResponse(string id, long? sequence, Exception exception)
    {
        var isTimeout = exception is TimeoutException;
        return new WorkerResponse
        {
            Id = id,
            Type = "error",
            Ok = false,
            Sequence = sequence,
            ErrorCode = exception.GetType().Name,
            ErrorMessage = exception.Message,
            ErrorKind = isTimeout ? "timeout" : "worker",
            Retryable = false
        };
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
            ["MIMO_TIMEOUT_SECONDS"] = timeoutSeconds.ToString(),
            ["MIMO_MAX_CONCURRENCY"] = Math.Max(1, _settings.Asr.MaxConcurrency).ToString()
        };

        return new PythonWorkerClient(
            executable,
            arguments,
            environment,
            Path.Combine(_dataDirectory, "logs", "worker.log"));
    }

    private async Task StartTranslationSessionAsync(CancellationToken cancellationToken)
    {
        var profile = _settings.Translation.ActiveProfile;
        if (!_settings.Translation.Enabled || profile is null)
        {
            TranslationStatusChanged?.Invoke(this, new TranslationRuntimeStatus("已关闭", "已关闭", 0, 0));
            return;
        }

        var session = new TranslationSession(
            profile,
            _settings.Asr.Language,
            _settings.Translation.TargetLanguage,
            () => TranslationWorkerClientFactory.Create(_baseDirectory, _dataDirectory),
            _history);
        session.TranslationReady += OnTranslationReady;
        session.StatusChanged += OnTranslationStatusChanged;
        session.TaskStatusChanged += OnTranslationTaskStatusChanged;
        session.DiagnosticEvent += OnTranslationDiagnosticEvent;
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            _translationSession = session;
        }
        catch (Exception ex)
        {
            session.TranslationReady -= OnTranslationReady;
            session.StatusChanged -= OnTranslationStatusChanged;
            session.TaskStatusChanged -= OnTranslationTaskStatusChanged;
            session.DiagnosticEvent -= OnTranslationDiagnosticEvent;
            await session.DisposeAsync().ConfigureAwait(false);
            throw new TranslationStartupException("翻译 worker 启动失败。", ex);
        }
    }

    private void OnTranslationReady(object? sender, TranslationResultUpdate update)
    {
        TranslationReady?.Invoke(this, update);
    }

    private void OnTranslationStatusChanged(object? sender, TranslationRuntimeStatus status)
    {
        TranslationStatusChanged?.Invoke(this, status);
    }

    private void OnTranslationTaskStatusChanged(object? sender, TranslationTaskStatusUpdate status)
    {
        TranslationTaskStatusChanged?.Invoke(this, status);
    }

    private void OnTranslationDiagnosticEvent(object? sender, TranslationDiagnosticUpdate update)
    {
        if (_translationDiagnosticWriter is null)
        {
            return;
        }
        try
        {
            var profile = _settings.Translation.ActiveProfile;
            var line = JsonSerializer.Serialize(new
            {
                update.Type,
                update.TaskId,
                update.UtteranceGroupId,
                update.SourceRevision,
                update.SourceText,
                update.Context,
                update.TargetLanguage,
                update.PromptVersion,
                update.EnqueuedAt,
                update.StartedAt,
                update.CompletedAt,
                update.TranslatedText,
                update.ErrorKind,
                update.WarningCodes,
                update.LatencyMs,
                profileId = profile?.Id,
                model = profile?.Model,
                finalEndpoint = profile is null ? null : TranslationProfileRules.BuildFinalEndpoint(profile.BaseUrl)
            }, DiagnosticJsonOptions);
            lock (_translationDiagnosticLock)
            {
                _translationDiagnosticWriter.WriteLine(line);
                _translationDiagnosticWriter.Flush();
            }
        }
        catch
        {
            // Translation diagnostics must not interrupt subtitle delivery.
        }
    }

    private void WriteTranslationMetrics(TranslationSession session)
    {
        try
        {
            var (p50, p95) = session.Metrics.LatencyPercentiles();
            var profile = _settings.Translation.ActiveProfile;
            var line = JsonSerializer.Serialize(new
            {
                type = "translation_session",
                timestamp = DateTimeOffset.Now,
                profileId = profile?.Id,
                model = profile?.Model,
                location = profile?.Location.ToString(),
                targetLanguage = _settings.Translation.TargetLanguage,
                session.Metrics.Successes,
                session.Metrics.Failures,
                session.Metrics.Retries,
                session.Metrics.StaleResults,
                session.Metrics.SameLanguageSkips,
                session.Metrics.BackpressureDrops,
                session.Metrics.StaleQueueDrops,
                session.Metrics.WorkerRestarts,
                session.Metrics.CircuitBreaks,
                session.Metrics.QueuePeak,
                latencyP50Ms = p50,
                latencyP95Ms = p95
            }, DiagnosticJsonOptions);
            var logsDirectory = Path.Combine(_dataDirectory, "logs");
            Directory.CreateDirectory(logsDirectory);
            File.AppendAllText(Path.Combine(logsDirectory, "translation-metrics.jsonl"), line + Environment.NewLine);
        }
        catch
        {
            // Metrics must not interrupt shutdown.
        }
    }

    private string FindWorkerScriptPath()
    {
        return FindWorkerScriptPath("asr_worker.py", "ASR");
    }

    private string FindWorkerScriptPath(string fileName, string workerName)
    {
        var directory = new DirectoryInfo(_baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "python", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{workerName} worker executable and python/{fileName} were not found.");
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
        var translationRoot = Path.Combine(_dataDirectory, "debug-translation");
        Directory.CreateDirectory(translationRoot);
        _translationDiagnosticWriter = File.CreateText(
            Path.Combine(translationRoot, $"session-{sessionId}.jsonl"));
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

    private DiagnosticSegmentRecord? SaveSegmentDiagnostic(
        long sequence,
        CompletedSpeechSegment segment,
        UtteranceGroupAssignment groupAssignment,
        byte[] wav)
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
            UtteranceGroupId = groupAssignment.UtteranceGroupId,
            UtteranceSegmentCount = groupAssignment.SegmentCount,
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
        if (_translationDiagnosticWriter is not null)
        {
            await _translationDiagnosticWriter.DisposeAsync().ConfigureAwait(false);
            _translationDiagnosticWriter = null;
        }

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
        public string UtteranceGroupId { get; init; } = "";
        public int UtteranceSegmentCount { get; init; }
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
        _workerRestartLock.Dispose();
        _captureRecoveryLock.Dispose();
        _subtitlePublishLock.Dispose();
    }
}

public sealed record VadEndpointRuntimeStatus(
    VadEndpointMode Mode,
    int EffectiveEndSilenceMs,
    bool IsAdaptive,
    EndpointAdjustment? Adjustment,
    EndpointEvaluation? Evaluation);
