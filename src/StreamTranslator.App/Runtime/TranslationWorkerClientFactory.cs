using System.IO;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.App.Runtime;

internal static class TranslationWorkerClientFactory
{
    public static TranslationWorkerClient Create(string baseDirectory, string dataDirectory)
    {
        var workerExe = Path.Combine(baseDirectory, "worker", "translation_worker.exe");
        var hasWorkerExe = File.Exists(workerExe);
        var workerScript = hasWorkerExe ? "" : FindWorkerScriptPath(baseDirectory);
        var configuredPython = Environment.GetEnvironmentVariable("STREAMTRANSLATOR_PYTHON");
        var executable = hasWorkerExe
            ? workerExe
            : string.IsNullOrWhiteSpace(configuredPython) ? "python" : configuredPython;
        return new TranslationWorkerClient(
            executable,
            hasWorkerExe ? "" : $"\"{workerScript}\"",
            Path.Combine(dataDirectory, "logs", "translation-worker.log"));
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
