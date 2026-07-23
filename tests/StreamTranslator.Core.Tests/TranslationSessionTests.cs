using System.Collections.Concurrent;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Subtitles;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class TranslationSessionTests
{
    [TestMethod]
    public async Task Revision_DiscardsStaleResultAndPublishesLatestTranslation()
    {
        var worker = new ControlledTranslationWorker();
        await using var session = CreateSession(worker);
        var updates = new ConcurrentQueue<TranslationResultUpdate>();
        session.TranslationReady += (_, update) => updates.Enqueue(update);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Hello"));
        var first = await worker.NextRequestAsync();
        session.Submit(Source(2, 2, "Hello again"));
        first.Complete(Success(first.Request, "旧译文"));
        var second = await worker.NextRequestAsync();
        second.Complete(Success(second.Request, "最新译文"));
        await WaitUntilAsync(() => updates.Count == 1);

        Assert.IsTrue(updates.TryDequeue(out var update));
        Assert.AreEqual(2, update.SourceRevision);
        Assert.AreEqual("最新译文", update.TranslatedText);
        Assert.AreEqual(1, session.Metrics.StaleResults);
    }

    [TestMethod]
    public async Task TransientFailure_RetriesOnceThenSucceeds()
    {
        var worker = new ScriptedTranslationWorker([
            Error("timeout", retryable: true),
            new TranslationWorkerResponse { Id = "ignored", Type = "translate_result", Ok = true, TranslatedText = "你好" }
        ]);
        await using var session = CreateSession(worker);
        var completion = new TaskCompletionSource<TranslationResultUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranslationReady += (_, update) => completion.TrySetResult(update);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Hello"));
        var update = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("你好", update.TranslatedText);
        Assert.AreEqual(2, worker.TranslateCalls);
        Assert.AreEqual(1, session.Metrics.Retries);
    }

    [TestMethod]
    public async Task TransientFailure_DoesNotRetryAfterTaskExpires()
    {
        var worker = new ControlledTranslationWorker();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00+08:00"));
        var policy = new TranslationSessionPolicy { TaskLifetime = TimeSpan.FromMilliseconds(50) };
        await using var session = CreateSession(worker, timeProvider: timeProvider, policy: policy);
        var statuses = new ConcurrentQueue<TranslationTaskStatusUpdate>();
        session.TaskStatusChanged += (_, status) => statuses.Enqueue(status);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Hello", timeProvider.GetUtcNow()));
        var firstAttempt = await worker.NextRequestAsync();
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        firstAttempt.Complete(Error("timeout", retryable: true));
        await WaitUntilAsync(() => statuses.Any(status => status.Status == "translation_dropped_stale_or_expired"));

        Assert.AreEqual(1, worker.TranslateCalls);
        Assert.AreEqual(0, session.Metrics.Retries);
    }

    [TestMethod]
    public async Task OlderRevisionSubmittedAfterLatest_DoesNotReplaceCurrentTask()
    {
        var worker = new ControlledTranslationWorker();
        await using var session = CreateSession(worker);
        await session.StartAsync();

        session.Submit(Source(2, 2, "Latest"));
        var latest = await worker.NextRequestAsync();
        session.Submit(Source(1, 1, "Late old revision"));
        latest.Complete(Success(latest.Request, "最新译文"));
        await WaitUntilAsync(() => session.Metrics.Successes == 1);
        await Task.Delay(50);

        Assert.AreEqual(1, worker.TranslateCalls);
    }

    [TestMethod]
    public async Task ThreeTerminalTransientFailures_OpenCircuitThenResumeAfterCooldown()
    {
        var responses = Enumerable.Repeat(Error("timeout", retryable: true), 6)
            .Append(new TranslationWorkerResponse
            {
                Id = "ignored",
                Type = "translate_result",
                Ok = true,
                TranslatedText = "恢复后的译文"
            });
        var worker = new ScriptedTranslationWorker(responses);
        var policy = new TranslationSessionPolicy
        {
            InitialCircuitCooldown = TimeSpan.FromMilliseconds(40),
            MaximumCircuitCooldown = TimeSpan.FromMilliseconds(80)
        };
        await using var session = CreateSession(worker, policy: policy);
        var completion = new TaskCompletionSource<TranslationResultUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranslationReady += (_, update) => completion.TrySetResult(update);
        await session.StartAsync();

        for (var index = 1; index <= 3; index++)
        {
            session.Submit(Source(index, 1, $"Source {index}") with
            {
                UtteranceGroupId = $"session:{index}"
            });
            await WaitUntilAsync(() => session.Metrics.Failures == index);
        }

        Assert.AreEqual(1, session.Metrics.CircuitBreaks);
        session.Submit(Source(4, 1, "Source 4") with { UtteranceGroupId = "session:4" });
        var update = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("session:4", update.UtteranceGroupId);
        Assert.AreEqual("恢复后的译文", update.TranslatedText);
        Assert.AreEqual(7, worker.TranslateCalls);
        Assert.AreEqual(1, session.Metrics.Successes);
    }

    [TestMethod]
    public async Task FailedHalfOpenProbe_ReopensCircuitImmediatelyThenRecovers()
    {
        var responses = Enumerable.Repeat(Error("timeout", retryable: true), 8)
            .Append(new TranslationWorkerResponse
            {
                Id = "ignored",
                Type = "translate_result",
                Ok = true,
                TranslatedText = "探测恢复"
            });
        var worker = new ScriptedTranslationWorker(responses);
        var policy = new TranslationSessionPolicy
        {
            InitialCircuitCooldown = TimeSpan.FromMilliseconds(30),
            MaximumCircuitCooldown = TimeSpan.FromMilliseconds(60)
        };
        await using var session = CreateSession(worker, policy: policy);
        var completion = new TaskCompletionSource<TranslationResultUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranslationReady += (_, update) => completion.TrySetResult(update);
        await session.StartAsync();

        for (var index = 1; index <= 3; index++)
        {
            session.Submit(Source(index, 1, $"Failure {index}") with
            {
                UtteranceGroupId = $"session:{index}"
            });
            await WaitUntilAsync(() => session.Metrics.Failures == index);
        }

        session.Submit(Source(4, 1, "Failed probe") with { UtteranceGroupId = "session:4" });
        await WaitUntilAsync(() => session.Metrics.Failures == 4);

        Assert.AreEqual(2, session.Metrics.CircuitBreaks);
        session.Submit(Source(5, 1, "Successful probe") with { UtteranceGroupId = "session:5" });
        var update = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("session:5", update.UtteranceGroupId);
        Assert.AreEqual("探测恢复", update.TranslatedText);
    }

    [TestMethod]
    public async Task HalfOpen_AllowsExactlyOneProbeAcrossConcurrentPumps()
    {
        var worker = new ControlledTranslationWorker();
        var policy = new TranslationSessionPolicy
        {
            InitialCircuitCooldown = TimeSpan.FromMilliseconds(30),
            MaximumCircuitCooldown = TimeSpan.FromMilliseconds(60)
        };
        await using var session = CreateSession(worker, policy: policy, maxConcurrency: 2);
        await session.StartAsync();

        for (var index = 1; index <= 3; index++)
        {
            session.Submit(Source(index, 1, $"Failure {index}") with
            {
                UtteranceGroupId = $"session:{index}"
            });
            var firstAttempt = await worker.NextRequestAsync();
            firstAttempt.Complete(Error("timeout", retryable: true));
            var retry = await worker.NextRequestAsync();
            retry.Complete(Error("timeout", retryable: true));
            await WaitUntilAsync(() => session.Metrics.Failures == index);
        }

        session.Submit(Source(4, 1, "Probe") with { UtteranceGroupId = "session:4" });
        session.Submit(Source(5, 1, "Waiting") with { UtteranceGroupId = "session:5" });
        var probe = await worker.NextRequestAsync();
        await Task.Delay(80);

        Assert.AreEqual(7, worker.TranslateCalls);
        probe.Complete(Success(probe.Request, "probe ok"));
        var afterProbe = await worker.NextRequestAsync();
        afterProbe.Complete(Success(afterProbe.Request, "next ok"));
        await WaitUntilAsync(() => session.Metrics.Successes == 2);
    }

    [TestMethod]
    public async Task FirstWorkerCrash_RestartsOnceWithoutResubmittingInFlightWork()
    {
        var failedWorker = new ThrowingTranslationWorker();
        var replacement = new ScriptedTranslationWorker([
            new TranslationWorkerResponse
            {
                Id = "ignored",
                Type = "translate_result",
                Ok = true,
                TranslatedText = "第二句译文"
            }
        ]);
        var workers = new ConcurrentQueue<ITranslationWorkerClient>([failedWorker, replacement]);
        await using var session = CreateSession(() =>
        {
            Assert.IsTrue(workers.TryDequeue(out var worker));
            return worker;
        });
        var updates = new ConcurrentQueue<TranslationResultUpdate>();
        session.TranslationReady += (_, update) => updates.Enqueue(update);
        await session.StartAsync();

        session.Submit(Source(1, 1, "First") with { UtteranceGroupId = "session:1" });
        await WaitUntilAsync(() => session.Metrics.WorkerRestarts == 1);
        session.Submit(Source(2, 1, "Second") with { UtteranceGroupId = "session:2" });
        await WaitUntilAsync(() => session.Metrics.Successes == 1);

        Assert.AreEqual(1, failedWorker.TranslateCalls);
        Assert.AreEqual(1, replacement.TranslateCalls);
        Assert.AreEqual("session:2", replacement.Requests.Single().UtteranceGroupId);
        Assert.IsFalse(updates.Any(update => update.UtteranceGroupId == "session:1"));
        Assert.IsTrue(updates.Any(update => update.UtteranceGroupId == "session:2"));
    }

    [TestMethod]
    public async Task Stop_DrainsInFlightTranslationBeforeTimeout()
    {
        var worker = new ControlledTranslationWorker();
        var policy = new TranslationSessionPolicy { DrainTimeout = TimeSpan.FromMilliseconds(500) };
        await using var session = CreateSession(worker, policy: policy);
        var completion = new TaskCompletionSource<TranslationResultUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranslationReady += (_, update) => completion.TrySetResult(update);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Hello"));
        var inFlight = await worker.NextRequestAsync();
        var stopTask = session.StopAsync();

        Assert.IsFalse(stopTask.IsCompleted);
        inFlight.Complete(Success(inFlight.Request, "你好"));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        var update = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("你好", update.TranslatedText);
        Assert.AreEqual(1, session.Metrics.Successes);
        Assert.AreEqual(1, worker.ShutdownCalls);
    }

    [TestMethod]
    public async Task Stop_CancelsInFlightTranslationAfterDrainTimeout()
    {
        var worker = new ControlledTranslationWorker();
        var policy = new TranslationSessionPolicy { DrainTimeout = TimeSpan.FromMilliseconds(40) };
        await using var session = CreateSession(worker, policy: policy);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Hello"));
        var inFlight = await worker.NextRequestAsync();
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(inFlight.Completion.Task.IsCanceled);
        Assert.AreEqual(0, session.Metrics.Successes);
        Assert.AreEqual(1, worker.ShutdownCalls);
    }

    [TestMethod]
    public async Task SameLanguage_DoesNotCallWorker()
    {
        var worker = new ScriptedTranslationWorker([]);
        await using var session = CreateSession(worker, sourceLanguage: "en", targetLanguage: "en");
        await session.StartAsync();

        session.Submit(Source(1, 1, "Welcome to the live stream."));
        await Task.Delay(50);

        Assert.AreEqual(0, worker.TranslateCalls);
        Assert.AreEqual(1, session.Metrics.SameLanguageSkips);
    }

    [TestMethod]
    public async Task Requests_UseLatestThreeContextGroupsWithinThirtySeconds()
    {
        var worker = new ScriptedTranslationWorker(Enumerable.Range(1, 5)
            .Select(index => new TranslationWorkerResponse
            {
                Id = "ignored",
                Type = "translate_result",
                Ok = true,
                TranslatedText = $"译文 {index}"
            }));
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00+08:00");
        await using var session = CreateSession(worker, timeProvider: new FixedTimeProvider(now));
        await session.StartAsync();

        for (var index = 1; index <= 5; index++)
        {
            session.Submit(Source(index, 1, $"Source {index}", now.AddSeconds(index)) with
            {
                UtteranceGroupId = $"session:{index}"
            });
            await WaitUntilAsync(() => worker.TranslateCalls == index);
        }

        var last = worker.Requests.Last();
        CollectionAssert.AreEqual(
            new[] { "session:2", "session:3", "session:4" },
            last.Context!.Select(item => item.UtteranceGroupId).ToArray());
        Assert.IsFalse(last.Context!.Any(item => item.UtteranceGroupId == "session:5"));
    }

    [TestMethod]
    public async Task BackloggedRequest_ContextNeverContainsFutureSubtitleGroups()
    {
        var worker = new ControlledTranslationWorker();
        await using var session = CreateSession(worker);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Source 1") with { UtteranceGroupId = "session:1" });
        var first = await worker.NextRequestAsync();
        for (var index = 2; index <= 4; index++)
        {
            session.Submit(Source(index, 1, $"Source {index}") with
            {
                UtteranceGroupId = $"session:{index}"
            });
        }
        first.Complete(Success(first.Request, "Translation 1"));

        var second = await worker.NextRequestAsync();
        CollectionAssert.AreEqual(
            new[] { "session:1" },
            second.Request.Context!.Select(item => item.UtteranceGroupId).ToArray());
        second.Complete(Success(second.Request, "Translation 2"));
        var third = await worker.NextRequestAsync();
        third.Complete(Success(third.Request, "Translation 3"));
        var fourth = await worker.NextRequestAsync();
        fourth.Complete(Success(fourth.Request, "Translation 4"));
    }

    [TestMethod]
    public async Task DuplicateTaskKey_IsIgnoredWhileInFlightAndAfterCompletion()
    {
        var worker = new ControlledTranslationWorker();
        await using var session = CreateSession(worker);
        await session.StartAsync();
        var source = Source(1, 1, "Hello");

        session.Submit(source);
        var inFlight = await worker.NextRequestAsync();
        session.Submit(source);
        inFlight.Complete(Success(inFlight.Request, "你好"));
        await WaitUntilAsync(() => session.Metrics.Successes == 1);
        await Task.Delay(50);

        session.Submit(source);
        await Task.Delay(50);

        Assert.AreEqual(1, worker.TranslateCalls);
        Assert.AreEqual(1, session.Metrics.Successes);
    }

    [TestMethod]
    public async Task QueueCapacity_DropsOldestPendingWorkWithoutBlockingNewestSource()
    {
        var worker = new ControlledTranslationWorker();
        await using var session = CreateSession(worker);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Source 1") with { UtteranceGroupId = "session:1" });
        var inFlight = await worker.NextRequestAsync();
        for (var index = 2; index <= 11; index++)
        {
            session.Submit(Source(index, 1, $"Source {index}") with { UtteranceGroupId = $"session:{index}" });
        }
        inFlight.Complete(Success(inFlight.Request, "one"));

        var translatedGroups = new List<string>();
        for (var index = 0; index < 8; index++)
        {
            var pending = await worker.NextRequestAsync();
            translatedGroups.Add(pending.Request.UtteranceGroupId!);
            pending.Complete(Success(pending.Request, "ok"));
        }

        CollectionAssert.DoesNotContain(translatedGroups, "session:2");
        CollectionAssert.DoesNotContain(translatedGroups, "session:3");
        CollectionAssert.Contains(translatedGroups, "session:11");
        Assert.AreEqual(2, session.Metrics.BackpressureDrops);
    }

    [TestMethod]
    public async Task VisibilityUpdate_PrefersEvictingSubtitleThatLeftFloatingWindow()
    {
        var worker = new ControlledTranslationWorker();
        await using var session = CreateSession(worker);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Source 1") with { UtteranceGroupId = "session:1" });
        var inFlight = await worker.NextRequestAsync();
        for (var index = 2; index <= 9; index++)
        {
            session.Submit(Source(index, 1, $"Source {index}") with
            {
                UtteranceGroupId = $"session:{index}"
            });
        }
        session.UpdateVisibleGroups(["session:8", "session:9"]);
        session.Submit(Source(10, 1, "Source 10") with { UtteranceGroupId = "session:10" });
        inFlight.Complete(Success(inFlight.Request, "one"));

        var translatedGroups = new List<string>();
        for (var index = 0; index < 8; index++)
        {
            var pending = await worker.NextRequestAsync();
            translatedGroups.Add(pending.Request.UtteranceGroupId!);
            pending.Complete(Success(pending.Request, "ok"));
        }

        CollectionAssert.DoesNotContain(translatedGroups, "session:2");
        CollectionAssert.Contains(translatedGroups, "session:8");
        CollectionAssert.Contains(translatedGroups, "session:9");
        CollectionAssert.Contains(translatedGroups, "session:10");
    }

    [TestMethod]
    public async Task ExpiredQueuedTask_IsDroppedBeforeWorkerDispatch()
    {
        var worker = new ControlledTranslationWorker();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00+08:00"));
        var policy = new TranslationSessionPolicy { TaskLifetime = TimeSpan.FromMilliseconds(50) };
        await using var session = CreateSession(worker, timeProvider: timeProvider, policy: policy);
        await session.StartAsync();

        session.Submit(Source(1, 1, "Source 1", timeProvider.GetUtcNow()) with
        {
            UtteranceGroupId = "session:1"
        });
        var inFlight = await worker.NextRequestAsync();
        session.Submit(Source(2, 1, "Source 2", timeProvider.GetUtcNow()) with
        {
            UtteranceGroupId = "session:2"
        });
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        inFlight.Complete(Success(inFlight.Request, "one"));
        await WaitUntilAsync(() => session.Metrics.BackpressureDrops == 1);

        Assert.AreEqual(1, worker.TranslateCalls);
        Assert.AreEqual(0, session.Metrics.QueueLength);
    }

    [TestMethod]
    public async Task SuccessfulResult_IsAppendedAndMaterializedThroughHistoryStore()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-translation-history-");
        var date = DateOnly.FromDateTime(DateTime.Now);
        var history = new SubtitleHistoryStore(directory.FullName);
        var source = Source(1, 1, "Hello");
        await history.AppendAsync(source, date);
        var worker = new ScriptedTranslationWorker([
            new TranslationWorkerResponse { Id = "ignored", Type = "translate_result", Ok = true, TranslatedText = "你好" }
        ]);
        var profile = new TranslationProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            BaseUrl = "http://127.0.0.1:8000/v1",
            Model = "model",
            Location = TranslationServiceLocation.Local,
            MaxConcurrency = 1
        };
        await using var session = new TranslationSession(profile, "en", "zh-Hans", () => worker, history);
        await session.StartAsync();

        session.Submit(source);
        await WaitUntilAsync(() => session.Metrics.Successes == 1);
        await session.StopAsync();
        var loaded = await history.LoadLatestAsync(date);

        Assert.AreEqual("你好", loaded.Single().TranslatedText);
    }

    [TestMethod]
    public async Task StaleResult_IsAppendedButNotMaterializedOverLatestRevision()
    {
        var directory = Directory.CreateTempSubdirectory("streamtranslator-stale-translation-history-");
        var history = new SubtitleHistoryStore(directory.FullName);
        var generatedAt = DateTimeOffset.Now;
        var date = DateOnly.FromDateTime(generatedAt.Date);
        var firstSource = Source(1, 1, "Hello", generatedAt);
        var revisedSource = Source(2, 2, "Hello again", generatedAt.AddSeconds(1)) with
        {
            Type = "subtitle_revision",
            ReplacesSequences = [1, 2]
        };
        await history.AppendAsync(firstSource, date);
        await history.AppendRevisionAsync(revisedSource, date);
        var worker = new ControlledTranslationWorker();
        await using var session = CreateSession(worker, history: history);
        await session.StartAsync();

        session.Submit(firstSource);
        var stale = await worker.NextRequestAsync();
        session.Submit(revisedSource);
        stale.Complete(Success(stale.Request, "旧译文"));
        var latest = await worker.NextRequestAsync();
        latest.Complete(Success(latest.Request, "最新译文"));
        await WaitUntilAsync(() => session.Metrics.Successes == 1);
        await session.StopAsync();

        var lines = await File.ReadAllLinesAsync(Path.Combine(directory.FullName, $"{date:yyyy-MM-dd}.jsonl"));
        var translationLines = lines.Where(line => line.Contains("\"type\":\"translation_result\"", StringComparison.Ordinal)).ToArray();
        var loaded = await history.LoadLatestAsync(date);

        Assert.AreEqual(2, translationLines.Length);
        Assert.IsTrue(translationLines.Any(line => line.Contains("旧译文", StringComparison.Ordinal)));
        Assert.AreEqual("最新译文", loaded.Single().TranslatedText);
    }

    [TestMethod]
    public async Task SecondWorkerCrash_DisablesTranslationWithoutThirdRestart()
    {
        var first = new ThrowingTranslationWorker();
        var second = new ThrowingTranslationWorker();
        var workers = new ConcurrentQueue<ITranslationWorkerClient>([first, second]);
        await using var session = CreateSession(() =>
        {
            Assert.IsTrue(workers.TryDequeue(out var worker));
            return worker;
        });
        var statuses = new ConcurrentQueue<TranslationTaskStatusUpdate>();
        session.TaskStatusChanged += (_, status) => statuses.Enqueue(status);
        await session.StartAsync();

        session.Submit(Source(1, 1, "First") with { UtteranceGroupId = "session:1" });
        await WaitUntilAsync(() => session.Metrics.WorkerRestarts == 1);
        session.Submit(Source(2, 1, "Second") with { UtteranceGroupId = "session:2" });
        await WaitUntilAsync(() => second.TranslateCalls == 1);
        await WaitUntilAsync(() => statuses.Count(status => status.Status == "translation_worker_crash") == 2);
        session.Submit(Source(3, 1, "Third") with { UtteranceGroupId = "session:3" });
        await Task.Delay(50);

        Assert.AreEqual(1, first.TranslateCalls);
        Assert.AreEqual(1, second.TranslateCalls);
        Assert.AreEqual(1, session.Metrics.WorkerRestarts);
        Assert.AreEqual(0, workers.Count);
    }

    private static TranslationSession CreateSession(
        ITranslationWorkerClient worker,
        string sourceLanguage = "en",
        string targetLanguage = "zh-Hans",
        TimeProvider? timeProvider = null,
        TranslationSessionPolicy? policy = null,
        SubtitleHistoryStore? history = null,
        int maxConcurrency = 1) =>
        CreateSession(
            () => worker,
            sourceLanguage,
            targetLanguage,
            timeProvider,
            policy,
            history,
            maxConcurrency);

    private static TranslationSession CreateSession(
        Func<ITranslationWorkerClient> workerFactory,
        string sourceLanguage = "en",
        string targetLanguage = "zh-Hans",
        TimeProvider? timeProvider = null,
        TranslationSessionPolicy? policy = null,
        SubtitleHistoryStore? history = null,
        int maxConcurrency = 1)
    {
        var profile = new TranslationProfile
        {
            Id = Guid.Parse("9a7a57da-5c95-4e44-9e3b-54795ae90998"),
            Name = "Test",
            BaseUrl = "http://127.0.0.1:8000/v1",
            Model = "model",
            Location = TranslationServiceLocation.Local,
            MaxConcurrency = maxConcurrency
        };
        return new TranslationSession(
            profile,
            sourceLanguage,
            targetLanguage,
            workerFactory,
            history,
            timeProvider ?? TimeProvider.System,
            policy);
    }

    private static SubtitleItem Source(
        long sequence,
        int revision,
        string text,
        DateTimeOffset? generatedAt = null) => new()
        {
            Sequence = sequence,
            UtteranceGroupId = "session:1",
            Revision = revision,
            ReplacesSequences = [sequence],
            GeneratedAt = generatedAt ?? DateTimeOffset.Now,
            SourceText = text,
            Status = SubtitleStatus.Final
        };

    private static TranslationWorkerResponse Success(TranslationWorkerRequest request, string text) => new()
    {
        Id = request.Id,
        Type = "translate_result",
        Ok = true,
        Sequence = request.Sequence,
        UtteranceGroupId = request.UtteranceGroupId,
        SourceRevision = request.SourceRevision,
        TargetLanguage = request.TargetLanguage,
        TranslatedText = text,
        LatencyMs = 10
    };

    private static TranslationWorkerResponse Error(string kind, bool retryable) => new()
    {
        Id = "ignored",
        Type = "error",
        Ok = false,
        ErrorKind = kind,
        Retryable = retryable
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ScriptedTranslationWorker(IEnumerable<TranslationWorkerResponse> responses)
        : ITranslationWorkerClient
    {
        private readonly ConcurrentQueue<TranslationWorkerResponse> _responses = new(responses);
        public List<TranslationWorkerRequest> Requests { get; } = [];
        public int TranslateCalls => Requests.Count;

        public Task<TranslationWorkerResponse> StartAsync(TranslationProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationWorkerResponse
            {
                Id = "cfg",
                Type = "configured",
                Ok = true,
                FinalEndpoint = "http://127.0.0.1:8000/v1/chat/completions"
            });

        public Task<TranslationWorkerResponse> TranslateAsync(TranslationWorkerRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No scripted response.");
            }
            return Task.FromResult(response with
            {
                Id = request.Id,
                Sequence = request.Sequence,
                UtteranceGroupId = request.UtteranceGroupId,
                SourceRevision = request.SourceRevision,
                TargetLanguage = request.TargetLanguage
            });
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledTranslationWorker : ITranslationWorkerClient
    {
        private readonly ConcurrentQueue<ControlledRequest> _requests = new();
        private readonly SemaphoreSlim _available = new(0);

        public int ShutdownCalls { get; private set; }
        public int TranslateCalls { get; private set; }

        public Task<TranslationWorkerResponse> StartAsync(TranslationProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationWorkerResponse { Id = "cfg", Type = "configured", Ok = true });

        public Task<TranslationWorkerResponse> TranslateAsync(TranslationWorkerRequest request, CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            var controlled = new ControlledRequest(request, cancellationToken);
            _requests.Enqueue(controlled);
            _available.Release();
            return controlled.Completion.Task;
        }

        public async Task<ControlledRequest> NextRequestAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _available.WaitAsync(timeout.Token);
            Assert.IsTrue(_requests.TryDequeue(out var request));
            return request;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCalls++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingTranslationWorker : ITranslationWorkerClient
    {
        public int TranslateCalls { get; private set; }

        public Task<TranslationWorkerResponse> StartAsync(
            TranslationProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationWorkerResponse { Id = "cfg", Type = "configured", Ok = true });

        public Task<TranslationWorkerResponse> TranslateAsync(
            TranslationWorkerRequest request,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            throw new InvalidOperationException("Worker exited unexpectedly.");
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledRequest
    {
        public ControlledRequest(TranslationWorkerRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Completion = new TaskCompletionSource<TranslationWorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => Completion.TrySetCanceled(cancellationToken));
        }

        public TranslationWorkerRequest Request { get; }
        public TaskCompletionSource<TranslationWorkerResponse> Completion { get; }
        public void Complete(TranslationWorkerResponse response) => Completion.TrySetResult(response);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
