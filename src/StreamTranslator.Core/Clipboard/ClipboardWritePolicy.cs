using System.Runtime.InteropServices;

namespace StreamTranslator.Core.Clipboard;

public sealed record ClipboardWriteOptions
{
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    internal void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "至少需要允许一次剪贴板写入尝试。");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "重试间隔不能为负数。");
        }
    }
}

public readonly record struct ClipboardWriteResult(bool Succeeded, Exception? Error);

public static class ClipboardWritePolicy
{
    public static async Task<ClipboardWriteResult> TryWriteAsync(
        string text,
        Action<string> write,
        ClipboardWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(write);

        options ??= new ClipboardWriteOptions();
        options.Validate();

        Exception? lastError = null;
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            try
            {
                write(text);
                return new ClipboardWriteResult(true, null);
            }
            catch (Exception exception) when (IsRecoverableClipboardException(exception))
            {
                lastError = exception;
                if (attempt == options.MaxAttempts)
                {
                    break;
                }

                if (options.RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(options.RetryDelay, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                return new ClipboardWriteResult(false, exception);
            }
        }

        return new ClipboardWriteResult(false, lastError);
    }

    private static bool IsRecoverableClipboardException(Exception exception)
    {
        return exception is ExternalException or InvalidOperationException;
    }
}
