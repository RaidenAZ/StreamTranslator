namespace StreamTranslator.Core.Worker;

public sealed record WorkerResponse
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public bool Ok { get; init; }
    public long? Sequence { get; init; }
    public string? Text { get; init; }
    public int? LatencyMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorKind { get; init; }
    public int? StatusCode { get; init; }
    public bool Retryable { get; init; }
}
