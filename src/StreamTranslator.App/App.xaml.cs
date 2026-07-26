using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace StreamTranslator.App;

public partial class App : Application
{
    /// <summary>
    /// Set by MainWindow once initialization finished. Before that point an
    /// unhandled dispatcher exception is a startup failure and the app exits;
    /// after it, a single UI handler fault must not kill a live caption session.
    /// </summary>
    internal static bool StartupCompleted { get; set; }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteFatalLog("UnhandledException", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteFatalLog("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteFatalLog("DispatcherUnhandledException", e.Exception);

        if (StartupCompleted)
        {
            ShowRuntimeError(e.Exception);
            e.Handled = true;
            return;
        }

        ShowFatalError(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void WriteFatalLog(string source, Exception exception)
    {
        try
        {
            var logsDirectory = Path.Combine(AppContext.BaseDirectory, "data", "logs");
            Directory.CreateDirectory(logsDirectory);
            File.AppendAllText(
                Path.Combine(logsDirectory, "fatal.log"),
                $"{DateTimeOffset.Now:O} {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Fatal logging must not trigger another startup failure.
        }
    }

    private static void ShowRuntimeError(Exception exception)
    {
        try
        {
            MessageBox.Show(
                $"发生未处理错误，详情已写入 data\\logs\\fatal.log，程序将继续运行。{Environment.NewLine}{exception.Message}",
                "StreamTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // The fatal log already captured the details.
        }
    }

    private static void ShowFatalError(Exception exception)
    {
        try
        {
            MessageBox.Show(
                $"StreamTranslator 启动失败，详情已写入 data\\logs\\fatal.log。{Environment.NewLine}{exception.Message}",
                "StreamTranslator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // If WPF cannot show a message box, the fatal log is still the source of truth.
        }
    }
}
