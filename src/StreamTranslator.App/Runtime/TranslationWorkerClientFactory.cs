using System.IO;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.App.Runtime;

internal static class TranslationWorkerClientFactory
{
    public static TranslationWorkerClient Create(string baseDirectory, string dataDirectory)
    {
        var workerExe = ResolvePackagedWorkerExe(baseDirectory);
        var hasWorkerExe = workerExe is not null;
        var workerScript = hasWorkerExe ? "" : FindWorkerScriptPath(baseDirectory);
        var configuredPython = Environment.GetEnvironmentVariable("STREAMTRANSLATOR_PYTHON");
        var executable = hasWorkerExe
            ? workerExe!
            : string.IsNullOrWhiteSpace(configuredPython) ? "python" : configuredPython;
        return new TranslationWorkerClient(
            executable,
            hasWorkerExe ? "" : $"\"{workerScript}\"",
            Path.Combine(dataDirectory, "logs", "translation-worker.log"));
    }

    private static string? ResolvePackagedWorkerExe(string baseDirectory)
    {
        // 优先使用 --onedir 目录（零 Temp 解压与残留），同时兼容旧版 --onefile 单文件布局
        var onedirExe = Path.Combine(baseDirectory, "worker", "translation_worker", "translation_worker.exe");
        if (File.Exists(onedirExe))
        {
            return onedirExe;
        }

        var onefileExe = Path.Combine(baseDirectory, "worker", "translation_worker.exe");
        return File.Exists(onefileExe) ? onefileExe : null;
    }

    private static string FindWorkerScriptPath(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "python", "translation_worker.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("worker/translation_worker.py was not found.");
    }
}
