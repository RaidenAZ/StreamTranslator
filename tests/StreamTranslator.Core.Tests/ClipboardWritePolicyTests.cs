using System.Runtime.InteropServices;
using StreamTranslator.Core.Clipboard;

namespace StreamTranslator.Core.Tests;

[TestClass]
public sealed class ClipboardWritePolicyTests
{
    [TestMethod]
    public async Task TryWriteAsync_RetriesClipboardBusyErrorAndSucceeds()
    {
        var attempts = 0;
        var writtenText = string.Empty;
        var busyError = new COMException("OpenClipboard failed", unchecked((int)0x800401D0));
        var options = new ClipboardWriteOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.Zero
        };

        var result = await ClipboardWritePolicy.TryWriteAsync(
            "today's subtitles",
            text =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw busyError;
                }

                writtenText = text;
            },
            options);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, attempts);
        Assert.AreEqual("today's subtitles", writtenText);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task TryWriteAsync_ReturnsFailureAfterClipboardBusyErrorExhaustsAttempts()
    {
        var attempts = 0;
        var busyError = new COMException("OpenClipboard failed", unchecked((int)0x800401D0));
        var options = new ClipboardWriteOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.Zero
        };

        var result = await ClipboardWritePolicy.TryWriteAsync(
            "today's subtitles",
            _ =>
            {
                attempts++;
                throw busyError;
            },
            options);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(3, attempts);
        Assert.AreSame(busyError, result.Error);
    }

    [TestMethod]
    public async Task TryWriteAsync_DoesNotRetryUnexpectedArgumentException()
    {
        var attempts = 0;
        var invalidTextError = new ArgumentException("Invalid clipboard text");
        var options = new ClipboardWriteOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.Zero
        };

        var result = await ClipboardWritePolicy.TryWriteAsync(
            "today's subtitles",
            _ =>
            {
                attempts++;
                throw invalidTextError;
            },
            options);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, attempts);
        Assert.AreSame(invalidTextError, result.Error);
    }
}
