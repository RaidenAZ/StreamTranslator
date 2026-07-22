using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class TranslationWorkerClientTests
{
    [TestMethod]
    public async Task Client_ConfiguresTranslatesAndRedactsSecretsFromStderr()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The portable application targets Windows.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"stream-translator-translation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "fake-translation-worker.ps1");
        var logPath = Path.Combine(root, "translation-worker.log");
        await File.WriteAllTextAsync(scriptPath, FakeWorkerScript);
        var profile = Profile() with { ApiKey = "super-secret-key" };

        try
        {
            await using var client = new TranslationWorkerClient(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                logPath);

            var configured = await client.StartAsync(profile);
            var translated = await client.TranslateAsync(TranslationWorkerRequest.Translate(
                "tr-1", 1, "session:1", 1, "en", "zh-Hans", "Hello", [], DateTimeOffset.Now));

            Assert.IsTrue(configured.Ok);
            Assert.AreEqual("http://127.0.0.1:8000/v1/chat/completions", configured.FinalEndpoint);
            Assert.AreEqual("translated", translated.TranslatedText);
            await client.ShutdownAsync();
            var log = await File.ReadAllTextAsync(logPath);
            Assert.IsFalse(log.Contains("super-secret-key", StringComparison.Ordinal));
            StringAssert.Contains(log, "***");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Client_UsesRealPythonWorkerAndOpenAiSdkAgainstFakeChatCompletionsServer()
    {
        var workerScript = FindRepositoryFile("python", "translation_worker.py");
        var python = Environment.GetEnvironmentVariable("STREAMTRANSLATOR_PYTHON") ?? "python";
        if (workerScript is null || !CanRunPython(python))
        {
            Assert.Inconclusive("Python translation worker integration prerequisites are unavailable.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"stream-translator-python-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var server = new FakeChatCompletionsServer();
        var profile = Profile() with
        {
            BaseUrl = $"http://127.0.0.1:{server.Port}/v1",
            MaxConcurrency = 1
        };

        try
        {
            await using var client = new TranslationWorkerClient(
                python,
                $"\"{workerScript}\"",
                Path.Combine(root, "translation-worker.log"));

            var configured = await client.StartAsync(profile);
            var translated = await client.TranslateAsync(TranslationWorkerRequest.Translate(
                "tr-real-1",
                1,
                "session:1",
                1,
                "en",
                "zh-Hans",
                "Hello",
                [],
                DateTimeOffset.Now));
            var request = await server.Request.WaitAsync(TimeSpan.FromSeconds(5));
            await client.ShutdownAsync();

            Assert.AreEqual($"http://127.0.0.1:{server.Port}/v1/chat/completions", configured.FinalEndpoint);
            Assert.AreEqual("/v1/chat/completions", request.Path);
            Assert.AreEqual("你好", translated.TranslatedText);
            using var body = JsonDocument.Parse(request.Body);
            Assert.AreEqual("model", body.RootElement.GetProperty("model").GetString());
            Assert.IsFalse(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.AreEqual(JsonValueKind.Array, body.RootElement.GetProperty("messages").ValueKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TranslationProfile Profile() => new()
    {
        Id = Guid.Parse("9a7a57da-5c95-4e44-9e3b-54795ae90998"),
        Name = "Local",
        BaseUrl = "http://127.0.0.1:8000/v1",
        Model = "model",
        Location = TranslationServiceLocation.Local
    };

    private static string? FindRepositoryFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static bool CanRunPython(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            return process is not null && process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class FakeChatCompletionsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeChatCompletionsServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Request = ReceiveAsync(_stop.Token);
        }

        public int Port { get; }
        public Task<CapturedRequest> Request { get; }

        private async Task<CapturedRequest> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
            var path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            var contentLength = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                }
            }

            var bodyBuffer = new char[contentLength];
            var bodyLength = 0;
            while (bodyLength < bodyBuffer.Length)
            {
                var read = await reader.ReadAsync(bodyBuffer.AsMemory(bodyLength), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                bodyLength += read;
            }
            var body = new string(bodyBuffer, 0, bodyLength);
            const string responseBody = """
                {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"\u4f60\u597d"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}
                """;
            var responseBytes = Encoding.UTF8.GetBytes(responseBody);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(responseBytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return new CapturedRequest(path, body);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await Request;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            _stop.Dispose();
        }
    }

    private sealed record CapturedRequest(string Path, string Body);

    private const string FakeWorkerScript = """
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            $request = $line | ConvertFrom-Json
            if ($request.type -eq 'configure') {
                [Console]::Error.WriteLine("key=$($request.profile.apiKey) Authorization: Bearer $($request.profile.apiKey)")
                [Console]::Out.WriteLine((@{ id = $request.id; type = 'configured'; ok = $true; profileId = $request.profile.profileId; finalEndpoint = 'http://127.0.0.1:8000/v1/chat/completions' } | ConvertTo-Json -Compress))
                continue
            }
            if ($request.type -eq 'translate') {
                [Console]::Out.WriteLine((@{ id = $request.id; type = 'translate_result'; ok = $true; sequence = $request.sequence; utteranceGroupId = $request.utteranceGroupId; sourceRevision = $request.sourceRevision; targetLanguage = $request.targetLanguage; translatedText = 'translated'; latencyMs = 8; warningCodes = @() } | ConvertTo-Json -Compress))
                continue
            }
            if ($request.type -eq 'shutdown') {
                [Console]::Out.WriteLine((@{ id = $request.id; type = 'shutdown'; ok = $true } | ConvertTo-Json -Compress))
                exit 0
            }
        }
        """;
}
