namespace StreamTranslator.Audio.Capture;

public sealed class AudioCaptureStoppedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}
