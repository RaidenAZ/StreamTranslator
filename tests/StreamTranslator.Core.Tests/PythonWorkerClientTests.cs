using StreamTranslator.Core.Worker;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class PythonWorkerClientTests
{
    [TestMethod]
    public async Task Client_CancelsPendingRequestAndShutsDownGracefully()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The portable application targets Windows.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"stream-translator-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "fake-worker.ps1");
        await File.WriteAllTextAsync(scriptPath, FakeWorkerScript);

        try
        {
            await using var client = new PythonWorkerClient(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                new Dictionary<string, string>());
            await client.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                () => client.TranscribeAsync(Request("ignore"), timeout.Token));
            await client.ShutdownAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Client_HandlesProtocolStderrCrashAndShutdown()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The portable application targets Windows.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"stream-translator-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "fake-worker.ps1");
        var logPath = Path.Combine(root, "worker.log");
        await File.WriteAllTextAsync(scriptPath, FakeWorkerScript);

        try
        {
            await using var client = new PythonWorkerClient(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                new Dictionary<string, string>(),
                logPath);
            await client.StartAsync();

            var response = await client.TranscribeAsync(Request("ok"));
            Assert.IsTrue(response.Ok);
            Assert.AreEqual(1, response.Sequence);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => client.TranscribeAsync(Request("crash")));
            StringAssert.Contains(await File.ReadAllTextAsync(logPath), "fake worker started");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Client_TimesOutWhenWorkerNeverReplies()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The portable application targets Windows.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"stream-translator-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "fake-worker.ps1");
        await File.WriteAllTextAsync(scriptPath, FakeWorkerScript);

        try
        {
            await using var client = new PythonWorkerClient(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                new Dictionary<string, string>(),
                stderrLogPath: null,
                requestTimeout: TimeSpan.FromMilliseconds(500));
            await client.StartAsync();

            await Assert.ThrowsExceptionAsync<TimeoutException>(
                () => client.TranscribeAsync(Request("ignore")));

            // The transport cap fails only that request; the client stays usable.
            var response = await client.TranscribeAsync(Request("ok-after-timeout"));
            Assert.IsTrue(response.Ok);
            await client.ShutdownAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Client_SurvivesMalformedStdoutLines()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The portable application targets Windows.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"stream-translator-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "fake-worker.ps1");
        await File.WriteAllTextAsync(scriptPath, FakeWorkerScript);

        try
        {
            await using var client = new PythonWorkerClient(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                new Dictionary<string, string>());
            await client.StartAsync();

            var noisy = await client.TranscribeAsync(Request("noise"));
            Assert.IsTrue(noisy.Ok);

            // The reader loop must stay alive after skipping the dirty lines.
            var second = await client.TranscribeAsync(Request("after-noise"));
            Assert.IsTrue(second.Ok);
            Assert.AreEqual(2, client.MalformedLineCount);
            await client.ShutdownAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WorkerRequest Request(string id)
    {
        return WorkerRequest.Transcribe(id, 1, 0, 1000, 16000, "auto", "AAAA");
    }

    private const string FakeWorkerScript = """
        [Console]::Error.WriteLine('fake worker started')
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            $request = $line | ConvertFrom-Json
            if ($request.type -eq 'ping') {
                [Console]::Out.WriteLine((@{ id = $request.id; type = 'ready'; ok = $true } | ConvertTo-Json -Compress))
                continue
            }
            if ($request.type -eq 'shutdown') {
                [Console]::Out.WriteLine((@{ id = $request.id; type = 'shutdown'; ok = $true } | ConvertTo-Json -Compress))
                exit 0
            }
            if ($request.id -eq 'crash') {
                exit 7
            }
            if ($request.id -eq 'ignore') {
                continue
            }
            if ($request.id -eq 'noise') {
                [Console]::Out.WriteLine('warning: some library printed noise to stdout')
                [Console]::Out.WriteLine('{"not":"a protocol message"}')
                [Console]::Out.WriteLine((@{ id = $request.id; type = 'transcribe_result'; ok = $true; sequence = $request.sequence; text = 'ok' } | ConvertTo-Json -Compress))
                continue
            }
            [Console]::Out.WriteLine((@{ id = $request.id; type = 'transcribe_result'; ok = $true; sequence = $request.sequence; text = 'ok' } | ConvertTo-Json -Compress))
        }
        """;
}
