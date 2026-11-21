using System.IO;
using StreamTranslator.Audio.Encoding;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Worker;

namespace StreamTranslator.App.Runtime;

internal static class AsrWorkerClientFactory
{
    public static PythonWorkerClient Create(
        string baseDirectory,
        string dataDirectory,
        AsrSettings asrSettings,
        int? overrideTimeoutMs = null)
    {
        var workerExe = ResolvePackagedWorkerExe(baseDirectory);
        var hasWorkerExe = workerExe is not null;
        var workerScript = hasWorkerExe ? "" : FindWorkerScriptPath(baseDirectory);
        var pythonExecutable = Environment.GetEnvironmentVariable("STREAMTRANSLATOR_PYTHON");
        var executable = hasWorkerExe
            ? workerExe!
            : string.IsNullOrWhiteSpace(pythonExecutable) ? "python" : pythonExecutable;
        var arguments = hasWorkerExe ? "" : $"\"{workerScript}\"";
        var effectiveTimeoutMs = overrideTimeoutMs ?? asrSettings.TimeoutMs;
        var timeoutSeconds = Math.Max(1, effectiveTimeoutMs / 1000);

        var environment = new Dictionary<string, string>
        {
            ["ASR_API_KEY"] = asrSettings.ActiveApiKey,
            ["ASR_BASE_URL"] = asrSettings.ActiveBaseUrl,
            ["ASR_MODEL"] = asrSettings.ActiveModel,
            ["ASR_PROVIDER"] = asrSettings.ActiveProviderType,
            ["ASR_TIMEOUT_SECONDS"] = timeoutSeconds.ToString(),
            ["ASR_MAX_CONCURRENCY"] = Math.Max(1, asrSettings.MaxConcurrency).ToString(),
        };

        return new PythonWorkerClient(
            executable,
            arguments,
            environment,
            Path.Combine(dataDirectory, "logs", "worker.log"),
            TimeSpan.FromMilliseconds(Math.Max(5000, effectiveTimeoutMs)));
    }

    /// <summary>
    /// 生成一段 480ms、16kHz 单声道 16-bit PCM WAV 探测音频，用于验证
    /// ASR 端点连通性、鉴权与模型配置是否可用。
    /// </summary>
    public static string CreateConnectionTestAudioBase64()
    {
        const int sampleRate = 16000;
        const int sampleCount = sampleRate * 480 / 1000;
        var samples = new short[sampleCount];
        // 极低幅度 440Hz 短音，确保非全零帧且不产生刺耳噪声，兼容各类 ASR 服务商
        for (var i = 0; i < sampleCount; i++)
        {
            var envelope = Math.Sin(Math.PI * i / sampleCount);
            samples[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 260.0 * envelope);
        }

        var wav = WavEncoder.EncodePcm16Mono(samples, sampleRate);
        return Convert.ToBase64String(wav);
    }

    private static string? ResolvePackagedWorkerExe(string baseDirectory)
    {
        // 优先使用 --onedir 目录（零 Temp 解压与残留），同时兼容旧版 --onefile 单文件布局
        var onedirExe = Path.Combine(baseDirectory, "worker", "asr_worker", "asr_worker.exe");
        if (File.Exists(onedirExe))
        {
            return onedirExe;
        }

        var onefileExe = Path.Combine(baseDirectory, "worker", "asr_worker.exe");
        return File.Exists(onefileExe) ? onefileExe : null;
    }

    private static string FindWorkerScriptPath(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
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
}
