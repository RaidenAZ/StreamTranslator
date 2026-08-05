using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Translation;

public sealed class TranslationSession : IAsyncDisposable
{
    public const int DefaultQueueCapacity = 8;
    public static readonly TimeSpan DefaultTaskLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(3);
    private readonly TranslationProfile _profile;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly Func<ITranslationWorkerClient> _workerFactory;
    private readonly SubtitleHistoryStore? _history;
    private readonly TimeProvider _timeProvider;
    private readonly TranslationSessionPolicy _policy;
    private readonly object _stateLock = new();
    private readonly object _historyTasksLock = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly SemaphoreSlim _workerRecoveryLock = new(1, 1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(30);
    private readonly LinkedList<TranslationWorkItem> _pending = [];
    private readonly Dictionary<string, DateTimeOffset> _taskKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Revision, DateTimeOffset UpdatedAt)> _latestRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TranslationContextState> _context = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPruneAt;
    private readonly List<Task> _pumpTasks = [];
    private readonly List<Task> _historyTasks = [];
    private CancellationTokenSource? _sessionCts;
    private ITranslationWorkerClient? _worker;
    private bool _accepting;
    private bool _stopping;
    private bool _translationDisabled;
    private int _workerRestartCount;
    private int _consecutiveTransientFailures;
    private TimeSpan _breakerCooldown = TimeSpan.FromSeconds(10);
    private DateTimeOffset? _breakerUntil;
    private CircuitState _circuitState = CircuitState.Closed;
    private long _circuitGeneration;
    private TaskCompletionSource<bool>? _probeCompletion;

    public TranslationSession(
        TranslationProfile profile,
        string sourceLanguage,
        string targetLanguage,
        Func<ITranslationWorkerClient> workerFactory,
        SubtitleHistoryStore? history,
        TimeProvider? timeProvider = null,
        TranslationSessionPolicy? policy = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));
        _history = history;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _policy = policy ?? new TranslationSessionPolicy();
        _breakerCooldown = _policy.InitialCircuitCooldown;
    }

    public event EventHandler<TranslationResultUpdate>? TranslationReady;
    public event EventHandler<TranslationTaskStatusUpdate>? TaskStatusChanged;
    public event EventHandler<TranslationDiagnosticUpdate>? DiagnosticEvent;
    public event EventHandler<TranslationRuntimeStatus>? StatusChanged;

    public TranslationMetrics Metrics { get; } = new();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var validationErrors = TranslationProfileRules.Validate(_profile);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        }

        lock (_stateLock)
        {
            if (_accepting || _worker is not null)
            {
                throw new InvalidOperationException("Translation session is already running.");
            }
            _sessionCts = new CancellationTokenSource();
            _accepting = true;
            _stopping = false;
            _translationDisabled = false;
            _workerRestartCount = 0;
            _consecutiveTransientFailures = 0;
            _breakerCooldown = _policy.InitialCircuitCooldown;
            _breakerUntil = null;
            _circuitState = CircuitState.Closed;
            _circuitGeneration = 0;
            _probeCompletion = null;
        }

        var worker = _workerFactory();
        try
        {
            await worker.StartAsync(_profile, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _accepting = false;
                _sessionCts?.Dispose();
                _sessionCts = null;
            }
            throw;
        }

        lock (_stateLock)
        {
            _worker = worker;
        }

        for (var index = 0; index < _profile.MaxConcurrency; index++)
        {
            _pumpTasks.Add(Task.Run(() => PumpAsync(_sessionCts!.Token), CancellationToken.None));
        }
        PublishStatus("已启动", "等待请求");
    }

    public void Submit(SubtitleItem source, bool isVisible = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Status != SubtitleStatus.Final ||
            string.IsNullOrWhiteSpace(source.SourceText) ||
            string.IsNullOrWhiteSpace(source.UtteranceGroupId))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var generatedAt = source.GeneratedAt ?? now;
        var work = new TranslationWorkItem(
            source.Sequence,
            source.UtteranceGroupId,
            source.Revision,
            source.SourceText,
            generatedAt,
            now,
            isVisible,
            CreateTaskKey(source.UtteranceGroupId, source.Revision),
            source.PreviousSourceTail);
        List<TranslationWorkItem> dropped = [];

        lock (_stateLock)
        {
            if (!_accepting || _translationDisabled)
            {
                return;
            }

            PruneExpiredStateLocked(now);

            if (_latestRevisions.TryGetValue(work.UtteranceGroupId, out var latest) &&
                work.SourceRevision < latest.Revision)
            {
                return;
            }

            _latestRevisions[work.UtteranceGroupId] = (work.SourceRevision, now);
            _context[work.UtteranceGroupId] = new TranslationContextState(
                work.Sequence,
                new TranslationContextItem(
                    work.UtteranceGroupId,
                    work.SourceText,
                    null,
                    work.GeneratedAt));
            RemoveQueuedOlderRevisionsLocked(work.UtteranceGroupId, work.SourceRevision, dropped);
        }

        foreach (var old in dropped)
        {
            Metrics.IncrementStaleQueueDrops();
            RecordStatus(old, "translation_dropped_stale_revision");
        }

        if (SourceLanguageDecision.ShouldSkip(_sourceLanguage, _targetLanguage, source.SourceText))
        {
            Metrics.IncrementSameLanguageSkips();
            RecordStatus(work, "translation_skipped_same_language");
            PublishStatus("已启动", "同语言，无需翻译");
            return;
        }

        dropped.Clear();
        var queued = false;
        lock (_stateLock)
        {
            if (!_accepting || _translationDisabled || _taskKeys.ContainsKey(work.Key))
            {
                return;
            }

            PurgeInvalidPendingLocked(now, dropped);
            while (_pending.Count >= _policy.QueueCapacity)
            {
                var candidate = _pending.FirstOrDefault(item => !item.IsVisible) ?? _pending.First!.Value;
                RemovePendingLocked(candidate);
                dropped.Add(candidate);
            }

            _pending.AddLast(work);
            _taskKeys[work.Key] = now;
            Metrics.SetQueueLength(_pending.Count);
            queued = true;
        }

        foreach (var old in dropped)
        {
            Metrics.IncrementBackpressureDrops();
            RecordStatus(old, "translation_dropped_backpressure");
        }

        if (queued)
        {
            _queueSignal.Release();
            PublishStatus(
                "已启动",
                dropped.Count > 0
                    ? $"翻译较慢，已丢弃 {dropped.Count} 条任务"
                    : Metrics.QueueLength > 0 ? $"队列 {Metrics.QueueLength}" : "等待请求");
        }
    }

    public void UpdateVisibleGroups(IReadOnlyCollection<string> visibleGroupIds)
    {
        ArgumentNullException.ThrowIfNull(visibleGroupIds);
        var visible = visibleGroupIds.ToHashSet(StringComparer.Ordinal);
        lock (_stateLock)
        {
            var node = _pending.First;
            while (node is not null)
            {
                var next = node.Next;
                var item = node.Value;
                var isVisible = visible.Contains(item.UtteranceGroupId);
                if (item.IsVisible != isVisible)
                {
                    node.Value = item with { IsVisible = isVisible };
                }
                node = next;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? sessionCts;
        Task[] pumps;
        lock (_stateLock)
        {
            if (_sessionCts is null && _worker is null)
            {
                return;
            }
            _accepting = false;
            _stopping = true;
            sessionCts = _sessionCts;
            pumps = _pumpTasks.ToArray();
        }

        for (var index = 0; index < Math.Max(1, pumps.Length); index++)
        {
            _queueSignal.Release();
        }

        if (pumps.Length > 0)
        {
            try
            {
                await Task.WhenAll(pumps).WaitAsync(_policy.DrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                sessionCts?.Cancel();
                for (var index = 0; index < pumps.Length; index++)
                {
                    _queueSignal.Release();
                }
                await ObserveAllAsync(pumps).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sessionCts?.Cancel();
                throw;
            }
        }

        await WaitForHistoryTasksAsync().ConfigureAwait(false);
        ITranslationWorkerClient? worker;
        lock (_stateLock)
        {
            worker = _worker;
            _worker = null;
        }
        if (worker is not null)
        {
            await worker.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            _pending.Clear();
            _taskKeys.Clear();
            _latestRevisions.Clear();
            _context.Clear();
            _pumpTasks.Clear();
            _sessionCts?.Dispose();
            _sessionCts = null;
            _stopping = false;
        }
        Metrics.SetQueueLength(0);
        PublishStatus("已停止", "已停止");
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var work = await DequeueAsync(cancellationToken).ConfigureAwait(false);
            if (work is null)
            {
                return;
            }
            await ProcessAsync(work, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TranslationWorkItem?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            List<TranslationWorkItem> dropped = [];
            TranslationWorkItem? result = null;
            lock (_stateLock)
            {
                PurgeInvalidPendingLocked(_timeProvider.GetUtcNow(), dropped);
                if (_pending.First is { } first)
                {
                    result = first.Value;
                    _pending.RemoveFirst();
                    Metrics.SetQueueLength(_pending.Count);
                }
                else if (_stopping)
                {
                    return null;
                }
            }

            foreach (var item in dropped)
            {
                Metrics.IncrementBackpressureDrops();
                RecordStatus(item, "translation_dropped_expired");
            }
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    private async Task ProcessAsync(TranslationWorkItem work, CancellationToken cancellationToken)
    {
        if (!IsCurrentAndValid(work))
        {
            RecordStatus(work, "translation_dropped_stale_or_expired");
            return;
        }

        var circuitPermit = await AcquireCircuitPermitAsync(work, cancellationToken).ConfigureAwait(false);
        if (!circuitPermit.Allowed)
        {
            RecordStatus(work, "translation_dropped_circuit_open");
            return;
        }

        var context = BuildContext(work);
        var request = TranslationWorkerRequest.Translate(
            $"tr-{work.UtteranceGroupId}-{work.SourceRevision}-{Guid.NewGuid():N}",
            work.Sequence,
            work.UtteranceGroupId,
            work.SourceRevision,
            _sourceLanguage,
            _targetLanguage,
            work.SourceText,
            context,
            work.EnqueuedAt,
            previousSource: work.PreviousSourceTail);
        DiagnosticEvent?.Invoke(this, new TranslationDiagnosticUpdate(
            "translation_request",
            request.Id,
            work.UtteranceGroupId,
            work.SourceRevision,
            work.SourceText,
            context,
            _targetLanguage,
            TranslationPrompt.Version,
            work.EnqueuedAt,
            _timeProvider.GetUtcNow(),
            null,
            null,
            null,
            [],
            null));
        TranslationWorkerResponse? response = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!IsCurrentAndValid(work))
            {
                RecordStatus(work, "translation_dropped_stale_or_expired");
                return;
            }
            if (attempt > 0)
            {
                Metrics.IncrementRetries();
            }

            ITranslationWorkerClient? worker;
            lock (_stateLock)
            {
                worker = _worker;
            }
            if (worker is null)
            {
                RecordStatus(work, "translation_worker_unavailable");
                return;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(_profile.TimeoutMs));
                response = await worker.TranslateAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response = Failure(request, "timeout", retryable: true, "Translation request timed out.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await RecoverWorkerAsync(worker, cancellationToken).ConfigureAwait(false);
                RecordStatus(work, "translation_worker_crash", "worker", ex.Message);
                return;
            }

            if (response.Ok || !response.Retryable || attempt == 1)
            {
                break;
            }
        }

        if (response is null)
        {
            return;
        }
        if (!response.Ok || string.IsNullOrWhiteSpace(response.TranslatedText))
        {
            RegisterTerminalFailure(response, circuitPermit);
            Metrics.IncrementFailures();
            RecordStatus(work, "translation_failed", response.ErrorKind, response.ErrorMessage);
            PublishDiagnosticResult(work, request.Id, context, response, null);
            PublishStatus("已启动", $"错误: {response.ErrorKind ?? "unknown"}");
            return;
        }

        if (!IsCurrentRevision(work))
        {
            Metrics.IncrementStaleResults();
            RecordStatus(work, "translation_result_stale");
            RecordResult(work, response.TranslatedText!, _timeProvider.GetUtcNow());
            PublishDiagnosticResult(work, request.Id, context, response, "translation_result_stale");
            return;
        }

        RegisterSuccess(circuitPermit);
        Metrics.IncrementSuccesses(response.LatencyMs);
        lock (_stateLock)
        {
            if (_context.TryGetValue(work.UtteranceGroupId, out var existingContext) &&
                IsCurrentRevisionLocked(work))
            {
                _context[work.UtteranceGroupId] = existingContext with
                {
                    Item = existingContext.Item with { TranslatedText = response.TranslatedText }
                };
            }
        }

        var completedAt = _timeProvider.GetUtcNow();
        RecordResult(work, response.TranslatedText, completedAt);
        PublishDiagnosticResult(work, request.Id, context, response, null);
        TranslationReady?.Invoke(this, new TranslationResultUpdate(
            work.Sequence,
            work.UtteranceGroupId,
            work.SourceRevision,
            response.TranslatedText,
            _targetLanguage,
            completedAt,
            response.LatencyMs,
            response.WarningCodes));
        PublishStatus("已启动", $"正常 ({response.LatencyMs?.ToString() ?? "-"} ms)");
    }

    private IReadOnlyList<TranslationContextItem> BuildContext(TranslationWorkItem current)
    {
        var cutoff = _timeProvider.GetUtcNow() - _policy.ContextLifetime;
        lock (_stateLock)
        {
            return _context.Values
                .Where(item => item.Sequence < current.Sequence)
                .Where(item => item.Item.GeneratedAt >= cutoff)
                .OrderBy(item => item.Sequence)
                .TakeLast(3)
                .Select(item => item.Item)
                .ToArray();
        }
    }

    private void PublishDiagnosticResult(
        TranslationWorkItem work,
        string taskId,
        IReadOnlyList<TranslationContextItem> context,
        TranslationWorkerResponse response,
        string? status)
    {
        DiagnosticEvent?.Invoke(this, new TranslationDiagnosticUpdate(
            "translation_result",
            taskId,
            work.UtteranceGroupId,
            work.SourceRevision,
            work.SourceText,
            context,
            _targetLanguage,
            TranslationPrompt.Version,
            work.EnqueuedAt,
            null,
            _timeProvider.GetUtcNow(),
            response.TranslatedText,
            status ?? response.ErrorKind,
            response.WarningCodes,
            response.LatencyMs));
    }

    private async Task<CircuitPermit> AcquireCircuitPermitAsync(
        TranslationWorkItem work,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TaskCompletionSource<bool>? probeCompletion = null;
            TimeSpan? delay = null;
            lock (_stateLock)
            {
                if (_translationDisabled || !IsCurrentRevisionLocked(work))
                {
                    return CircuitPermit.Denied;
                }

                var now = _timeProvider.GetUtcNow();
                switch (_circuitState)
                {
                    case CircuitState.Closed:
                        return new CircuitPermit(true, false, _circuitGeneration);
                    case CircuitState.HalfOpen:
                        probeCompletion = _probeCompletion;
                        break;
                    default:
                        if (_breakerUntil is null || _breakerUntil <= now)
                        {
                            _circuitState = CircuitState.HalfOpen;
                            _probeCompletion = new TaskCompletionSource<bool>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                            return new CircuitPermit(true, true, _circuitGeneration);
                        }
                        delay = _breakerUntil.Value - now;
                        break;
                }
            }

            var remainingLife = _policy.TaskLifetime - (_timeProvider.GetUtcNow() - work.EnqueuedAt);
            if (remainingLife <= TimeSpan.Zero)
            {
                return CircuitPermit.Denied;
            }

            if (probeCompletion is not null)
            {
                try
                {
                    await probeCompletion.Task.WaitAsync(remainingLife, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return CircuitPermit.Denied;
                }
                continue;
            }

            if (delay is null || delay.Value >= remainingLife)
            {
                return CircuitPermit.Denied;
            }
            PublishStatus("已启动", $"熔断冷却 {Math.Ceiling(delay.Value.TotalSeconds)} 秒");
            await Task.Delay(delay.Value, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return CircuitPermit.Denied;
    }

    private void RegisterTerminalFailure(TranslationWorkerResponse response, CircuitPermit permit)
    {
        lock (_stateLock)
        {
            if (response.Retryable && permit.Generation == _circuitGeneration)
            {
                if (permit.IsProbe && _circuitState == CircuitState.HalfOpen)
                {
                    OpenCircuitLocked();
                }
                else if (!permit.IsProbe && _circuitState == CircuitState.Closed)
                {
                    _consecutiveTransientFailures++;
                    if (_consecutiveTransientFailures >= 3)
                    {
                        OpenCircuitLocked();
                    }
                }
            }
            else if (!response.Retryable &&
                     response.ErrorKind is "authentication" or "model_not_found" or "endpoint_not_found" or
                     "configuration" or "protocol" or "invalid_response")
            {
                _translationDisabled = true;
                CompleteProbeLocked(false);
                DropAllPendingLocked("translation_disabled_fatal_error");
            }
        }
    }

    private void RegisterSuccess(CircuitPermit permit)
    {
        lock (_stateLock)
        {
            if (permit.Generation != _circuitGeneration)
            {
                return;
            }

            if (permit.IsProbe && _circuitState == CircuitState.HalfOpen)
            {
                _circuitState = CircuitState.Closed;
                _breakerUntil = null;
                _consecutiveTransientFailures = 0;
                _breakerCooldown = _policy.InitialCircuitCooldown;
                CompleteProbeLocked(true);
            }
            else if (!permit.IsProbe && _circuitState == CircuitState.Closed)
            {
                _consecutiveTransientFailures = 0;
                _breakerUntil = null;
                _breakerCooldown = _policy.InitialCircuitCooldown;
            }
        }
    }

    private void OpenCircuitLocked()
    {
        _circuitState = CircuitState.Open;
        _breakerUntil = _timeProvider.GetUtcNow() + _breakerCooldown;
        _breakerCooldown = TimeSpan.FromSeconds(Math.Min(
            _policy.MaximumCircuitCooldown.TotalSeconds,
            _breakerCooldown.TotalSeconds * 2));
        _consecutiveTransientFailures = 0;
        _circuitGeneration++;
        Metrics.IncrementCircuitBreaks();
        CompleteProbeLocked(false);
    }

    private void CompleteProbeLocked(bool result)
    {
        var completion = _probeCompletion;
        _probeCompletion = null;
        completion?.TrySetResult(result);
    }

    private async Task RecoverWorkerAsync(ITranslationWorkerClient failedWorker, CancellationToken cancellationToken)
    {
        await _workerRecoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (!ReferenceEquals(_worker, failedWorker))
                {
                    return;
                }
                if (_workerRestartCount >= 1)
                {
                    _translationDisabled = true;
                    DropAllPendingLocked("translation_disabled_worker_crash");
                    PublishStatus("异常", "翻译进程重复退出，本会话已停用翻译");
                    return;
                }
                _workerRestartCount++;
            }

            await failedWorker.DisposeAsync().ConfigureAwait(false);
            var replacement = _workerFactory();
            try
            {
                await replacement.StartAsync(_profile, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                lock (_stateLock)
                {
                    _translationDisabled = true;
                    DropAllPendingLocked("translation_disabled_worker_restart_failed");
                }
                PublishStatus("异常", "翻译进程重启失败，本会话已停用翻译");
                return;
            }

            lock (_stateLock)
            {
                _worker = replacement;
            }
            Metrics.IncrementWorkerRestarts();
            PublishStatus("已重启", "等待请求");
        }
        finally
        {
            _workerRecoveryLock.Release();
        }
    }

    private bool IsCurrentAndValid(TranslationWorkItem work)
    {
        lock (_stateLock)
        {
            return !_translationDisabled &&
                   IsCurrentRevisionLocked(work) &&
                   _timeProvider.GetUtcNow() - work.EnqueuedAt <= _policy.TaskLifetime;
        }
    }

    private bool IsCurrentRevision(TranslationWorkItem work)
    {
        lock (_stateLock)
        {
            return IsCurrentRevisionLocked(work);
        }
    }

    private bool IsCurrentRevisionLocked(TranslationWorkItem work)
    {
        return _latestRevisions.TryGetValue(work.UtteranceGroupId, out var latest) &&
               latest.Revision == work.SourceRevision;
    }

    private void PurgeInvalidPendingLocked(DateTimeOffset now, ICollection<TranslationWorkItem> dropped)
    {
        var node = _pending.First;
        while (node is not null)
        {
            var next = node.Next;
            var item = node.Value;
            if (!IsCurrentRevisionLocked(item) || now - item.EnqueuedAt > _policy.TaskLifetime)
            {
                _pending.Remove(node);
                _taskKeys.Remove(item.Key);
                dropped.Add(item);
            }
            node = next;
        }
        Metrics.SetQueueLength(_pending.Count);
    }

    private void RemoveQueuedOlderRevisionsLocked(
        string groupId,
        int revision,
        ICollection<TranslationWorkItem> dropped)
    {
        var node = _pending.First;
        while (node is not null)
        {
            var next = node.Next;
            if (string.Equals(node.Value.UtteranceGroupId, groupId, StringComparison.Ordinal) &&
                node.Value.SourceRevision < revision)
            {
                var item = node.Value;
                _pending.Remove(node);
                _taskKeys.Remove(item.Key);
                dropped.Add(item);
            }
            node = next;
        }
        Metrics.SetQueueLength(_pending.Count);
    }

    private void RemovePendingLocked(TranslationWorkItem item)
    {
        _pending.Remove(item);
        // A dropped (never processed) task must not keep blocking resubmission
        // of the same revision through the completed-task dedup set.
        _taskKeys.Remove(item.Key);
        Metrics.SetQueueLength(_pending.Count);
    }

    private void PruneExpiredStateLocked(DateTimeOffset now)
    {
        if (now - _lastPruneAt < PruneInterval)
        {
            return;
        }

        _lastPruneAt = now;

        // Utterance groups live at most 12 seconds; entries older than these
        // windows can never affect dedup, stale detection or context again.
        var retiredCutoff = now - _policy.RetiredStateLifetime;
        foreach (var entry in _taskKeys)
        {
            if (entry.Value < retiredCutoff)
            {
                _taskKeys.Remove(entry.Key);
            }
        }

        foreach (var entry in _latestRevisions)
        {
            if (entry.Value.UpdatedAt < retiredCutoff)
            {
                _latestRevisions.Remove(entry.Key);
            }
        }

        var contextCutoff = now - _policy.ContextLifetime * 2;
        foreach (var entry in _context)
        {
            if (entry.Value.Item.GeneratedAt < contextCutoff)
            {
                _context.Remove(entry.Key);
            }
        }
    }

    private void DropAllPendingLocked(string status)
    {
        var dropped = _pending.ToArray();
        _pending.Clear();
        _taskKeys.Clear();
        Metrics.SetQueueLength(0);
        foreach (var item in dropped)
        {
            RecordStatus(item, status);
        }
    }

    private string CreateTaskKey(string groupId, int revision)
    {
        return $"{groupId}|{revision}|{_targetLanguage}|{_profile.Id}";
    }

    private void RecordResult(TranslationWorkItem work, string text, DateTimeOffset completedAt)
    {
        if (_history is null)
        {
            return;
        }
        TrackHistoryTask(_history.AppendTranslationResultAsync(new TranslationHistoryEvent
        {
            UtteranceGroupId = work.UtteranceGroupId,
            SourceRevision = work.SourceRevision,
            TargetLanguage = _targetLanguage,
            TranslatedText = text,
            TranslationProfileId = _profile.Id,
            Model = _profile.Model,
            CompletedAt = completedAt
        }, DateOnly.FromDateTime(work.GeneratedAt.Date)));
    }

    private void RecordStatus(
        TranslationWorkItem work,
        string status,
        string? errorKind = null,
        string? errorMessage = null)
    {
        _ = errorMessage; // Detailed response summaries belong in diagnostics, not subtitle history.
        TaskStatusChanged?.Invoke(this, new TranslationTaskStatusUpdate(
            work.UtteranceGroupId,
            work.SourceRevision,
            status,
            errorKind,
            _timeProvider.GetUtcNow()));
        if (_history is null)
        {
            return;
        }
        TrackHistoryTask(_history.AppendTranslationStatusAsync(new TranslationStatusHistoryEvent
        {
            UtteranceGroupId = work.UtteranceGroupId,
            SourceRevision = work.SourceRevision,
            TargetLanguage = _targetLanguage,
            Status = status,
            ErrorKind = errorKind,
            CompletedAt = _timeProvider.GetUtcNow()
        }, DateOnly.FromDateTime(work.GeneratedAt.Date)));
    }

    private void TrackHistoryTask(Task task)
    {
        lock (_historyTasksLock)
        {
            _historyTasks.Add(task);
        }
        _ = task.ContinueWith(completed =>
        {
            lock (_historyTasksLock)
            {
                _historyTasks.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForHistoryTasksAsync()
    {
        Task[] tasks;
        lock (_historyTasksLock)
        {
            tasks = _historyTasks.ToArray();
        }
        await ObserveAllAsync(tasks).ConfigureAwait(false);
    }

    private void PublishStatus(string worker, string service)
    {
        StatusChanged?.Invoke(this, new TranslationRuntimeStatus(
            worker,
            service,
            Metrics.QueueLength,
            Metrics.QueuePeak));
    }

    private static TranslationWorkerResponse Failure(
        TranslationWorkerRequest request,
        string kind,
        bool retryable,
        string message) => new()
        {
            Id = request.Id,
            Type = "error",
            Ok = false,
            Sequence = request.Sequence,
            UtteranceGroupId = request.UtteranceGroupId,
            SourceRevision = request.SourceRevision,
            ErrorKind = kind,
            ErrorMessage = message,
            Retryable = retryable
        };

    private static async Task ObserveAllAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // History and worker cleanup failures must not block application shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _queueSignal.Dispose();
        _workerRecoveryLock.Dispose();
    }

    private sealed record TranslationWorkItem(
        long Sequence,
        string UtteranceGroupId,
        int SourceRevision,
        string SourceText,
        DateTimeOffset GeneratedAt,
        DateTimeOffset EnqueuedAt,
        bool IsVisible,
        string Key,
        string? PreviousSourceTail = null);

    private sealed record TranslationContextState(long Sequence, TranslationContextItem Item);

    private enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }

    private readonly record struct CircuitPermit(bool Allowed, bool IsProbe, long Generation)
    {
        public static CircuitPermit Denied { get; } = new(false, false, 0);
    }
}

public sealed record TranslationResultUpdate(
    long Sequence,
    string UtteranceGroupId,
    int SourceRevision,
    string TranslatedText,
    string TargetLanguage,
    DateTimeOffset CompletedAt,
    int? LatencyMs,
    IReadOnlyList<string> WarningCodes);

public sealed record TranslationTaskStatusUpdate(
    string UtteranceGroupId,
    int SourceRevision,
    string Status,
    string? ErrorKind,
    DateTimeOffset CompletedAt);

public sealed record TranslationDiagnosticUpdate(
    string Type,
    string TaskId,
    string UtteranceGroupId,
    int SourceRevision,
    string SourceText,
    IReadOnlyList<TranslationContextItem> Context,
    string TargetLanguage,
    string PromptVersion,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? TranslatedText,
    string? ErrorKind,
    IReadOnlyList<string> WarningCodes,
    int? LatencyMs);

public sealed record TranslationRuntimeStatus(
    string WorkerStatus,
    string ServiceStatus,
    int QueueLength,
    int QueuePeak);

public sealed record TranslationSessionPolicy
{
    public int QueueCapacity { get; init; } = TranslationSession.DefaultQueueCapacity;
    public TimeSpan TaskLifetime { get; init; } = TranslationSession.DefaultTaskLifetime;
    public TimeSpan DrainTimeout { get; init; } = TranslationSession.DefaultDrainTimeout;
    public TimeSpan ContextLifetime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InitialCircuitCooldown { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaximumCircuitCooldown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long completed-task dedup keys and latest-revision entries survive.
    /// Bounds session state during multi-hour captures; far larger than the
    /// 12-second utterance-group window, so dedup semantics are unaffected.
    /// </summary>
    public TimeSpan RetiredStateLifetime { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class TranslationMetrics
{
    private long _successes;
    private long _failures;
    private long _retries;
    private long _staleResults;
    private long _sameLanguageSkips;
    private long _backpressureDrops;
    private long _staleQueueDrops;
    private long _workerRestarts;
    private long _circuitBreaks;
    private int _queueLength;
    private int _queuePeak;
    private readonly object _latencyLock = new();
    private readonly List<int> _latencies = [];

    public long Successes => Interlocked.Read(ref _successes);
    public long Failures => Interlocked.Read(ref _failures);
    public long Retries => Interlocked.Read(ref _retries);
    public long StaleResults => Interlocked.Read(ref _staleResults);
    public long SameLanguageSkips => Interlocked.Read(ref _sameLanguageSkips);
    public long BackpressureDrops => Interlocked.Read(ref _backpressureDrops);
    public long StaleQueueDrops => Interlocked.Read(ref _staleQueueDrops);
    public long WorkerRestarts => Interlocked.Read(ref _workerRestarts);
    public long CircuitBreaks => Interlocked.Read(ref _circuitBreaks);
    public int QueueLength => Volatile.Read(ref _queueLength);
    public int QueuePeak => Volatile.Read(ref _queuePeak);

    public (int? P50, int? P95) LatencyPercentiles()
    {
        lock (_latencyLock)
        {
            if (_latencies.Count == 0)
            {
                return (null, null);
            }
            var sorted = _latencies.Order().ToArray();
            return (Percentile(sorted, 0.50), Percentile(sorted, 0.95));
        }
    }

    internal void IncrementSuccesses(int? latencyMs)
    {
        Interlocked.Increment(ref _successes);
        if (latencyMs is { } latency)
        {
            lock (_latencyLock)
            {
                _latencies.Add(latency);
            }
        }
    }

    internal void IncrementFailures() => Interlocked.Increment(ref _failures);
    internal void IncrementRetries() => Interlocked.Increment(ref _retries);
    internal void IncrementStaleResults() => Interlocked.Increment(ref _staleResults);
    internal void IncrementSameLanguageSkips() => Interlocked.Increment(ref _sameLanguageSkips);
    internal void IncrementBackpressureDrops() => Interlocked.Increment(ref _backpressureDrops);
    internal void IncrementStaleQueueDrops() => Interlocked.Increment(ref _staleQueueDrops);
    internal void IncrementWorkerRestarts() => Interlocked.Increment(ref _workerRestarts);
    internal void IncrementCircuitBreaks() => Interlocked.Increment(ref _circuitBreaks);

    internal void SetQueueLength(int value)
    {
        Volatile.Write(ref _queueLength, value);
        var peak = Volatile.Read(ref _queuePeak);
        while (value > peak)
        {
            var observed = Interlocked.CompareExchange(ref _queuePeak, value, peak);
            if (observed == peak)
            {
                break;
            }
            peak = observed;
        }
    }

    private static int Percentile(IReadOnlyList<int> sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
