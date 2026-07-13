namespace StreamTranslator.Core.Worker;

public sealed record WorkerRequest
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public long? Sequence { get; init; }
    public long? StartMs { get; init; }
    public long? EndMs { get; init; }
    public string? AudioFormat { get; init; }
    public int? SampleRate { get; init; }
    public string? Language { get; init; }
    public string? AudioBase64 { get; init; }

    public static WorkerRequest Ping(string id)
    {
        return new WorkerRequest { Id = id, Type = WorkerMessageTypes.Ping };
    }

    public static WorkerRequest Shutdown(string id)
    {
        return new WorkerRequest { Id = id, Type = WorkerMessageTypes.Shutdown };
    }

    public static WorkerRequest Transcribe(
        string id,
        long sequence,
        long startMs,
        long endMs,
        int sampleRate,
        string language,
        string audioBase64)
    {
        return new WorkerRequest
        {
            Id = id,
            Type = WorkerMessageTypes.Transcribe,
            Sequence = sequence,
            StartMs = startMs,
            EndMs = endMs,
            AudioFormat = "wav",
            SampleRate = sampleRate,
            Language = language,
            AudioBase64 = audioBase64
        };
    }
}

public static class WorkerMessageTypes
{
    public const string Ping = "ping";
    public const string Ready = "ready";
    public const string Shutdown = "shutdown";
    public const string Transcribe = "transcribe";
    public const string TranscribeResult = "transcribe_result";
}

