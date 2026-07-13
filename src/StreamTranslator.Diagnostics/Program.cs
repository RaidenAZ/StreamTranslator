using System.Text.Encodings.Web;
using System.Text.Json;
using CommandLine;
using NAudio.Wave;
using StreamTranslator.Audio.Capture;
using StreamTranslator.Audio.Encoding;
using StreamTranslator.Audio.Segmentation;
using StreamTranslator.Audio.Vad;

return Parser.Default.ParseArguments<SegmentOptions>(args)
    .MapResult(RunSegment, _ => 1);

static int RunSegment(SegmentOptions options)
{
    if (!File.Exists(options.Input))
    {
        Console.Error.WriteLine($"Input file not found: {options.Input}");
        return 2;
    }

    Directory.CreateDirectory(options.Output);
    var segmentsDirectory = Path.Combine(options.Output, "segments");
    var vadDirectory = Path.Combine(options.Output, "vad");
    var sessionsDirectory = Path.Combine(options.Output, "sessions");
    Directory.CreateDirectory(segmentsDirectory);
    Directory.CreateDirectory(vadDirectory);
    Directory.CreateDirectory(sessionsDirectory);

    using var vad = CreateVad(options);
    using var reader = new AudioFileReader(options.Input);
    var sourceSamples = ReadAudio(reader);
    using var normalizer = new StreamingAudioNormalizer(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    var normalized = normalizer.ProcessFloatSamples(sourceSamples);
    var frameBuffer = new PcmFrameBuffer(AudioNormalizer.TargetSampleRate, options.FrameMs);
    var segmenter = new SpeechSegmenter(new SpeechSegmenterOptions
    {
        EndSilenceMs = options.EndSilenceMs,
        StartSpeechMs = options.StartSpeechMs,
        PreRollMs = options.PreRollMs,
        MinSegmentMs = options.MinSegmentMs,
        SoftBreakSilenceMs = options.SoftBreakSilenceMs,
        SoftMaxSegmentMs = options.SoftMaxSegmentMs,
        HardMaxSegmentMs = options.HardMaxSegmentMs,
        OverlapMs = options.OverlapMs
    });

    var sessionId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
    var vadPath = Path.Combine(vadDirectory, $"session-{sessionId}.vad.jsonl");
    var sessionPath = Path.Combine(sessionsDirectory, $"session-{sessionId}.json");
    var metricsPath = Path.Combine(sessionsDirectory, $"session-{sessionId}.metrics.json");
    var segmentRecords = new List<SegmentRecord>();
    var sequence = 1;

    using var vadWriter = File.CreateText(vadPath);
    foreach (var frame in frameBuffer.Push(normalized))
    {
        var decision = vad.Analyze(frame.Samples, frame.SampleRate);
        vadWriter.WriteLine(JsonSerializer.Serialize(new VadRecord(frame.StartMs, decision.Probability, decision.IsSpeech), JsonOptions.Default));

        var completed = segmenter.Push(frame, decision);
        if (completed is null)
        {
            continue;
        }

        var segmentFileName = $"seg-{sequence:000000}.wav";
        var segmentPath = Path.Combine(segmentsDirectory, segmentFileName);
        File.WriteAllBytes(segmentPath, WavEncoder.EncodePcm16Mono(completed.Samples, completed.SampleRate));
        segmentRecords.Add(new SegmentRecord(
            sequence,
            completed.StartMs,
            completed.EndMs,
            completed.EndMs - completed.StartMs,
            completed.CutReason.ToString(),
            completed.OverlapMs,
            segmentFileName));
        sequence++;
    }

    var session = new
    {
        input = options.Input,
        output = options.Output,
        sampleRate = AudioNormalizer.TargetSampleRate,
        frameMs = options.FrameMs,
        options.StartSpeechMs,
        options.PreRollMs,
        options.EndSilenceMs,
        options.SoftBreakSilenceMs,
        options.MinSegmentMs,
        options.SoftMaxSegmentMs,
        options.HardMaxSegmentMs,
        options.OverlapMs,
        segmentCount = segmentRecords.Count,
        segments = segmentRecords
    };

    var metrics = new
    {
        totalAudioDurationMs = normalized.Length * 1000L / AudioNormalizer.TargetSampleRate,
        segmentCount = segmentRecords.Count,
        averageSegmentDurationMs = segmentRecords.Count == 0 ? 0 : segmentRecords.Average(item => item.DurationMs),
        tooShortSegmentCount = segmentRecords.Count(item => item.DurationMs < options.MinSegmentMs),
        hardCutCount = segmentRecords.Count(item => item.CutReason == SpeechSegmentCutReason.HardMax.ToString()),
        softCutCount = segmentRecords.Count(item => item.CutReason == SpeechSegmentCutReason.SoftMax.ToString()),
        silenceCutCount = segmentRecords.Count(item => item.CutReason == SpeechSegmentCutReason.Silence.ToString()),
        overlapCount = segmentRecords.Count(item => item.OverlapMs > 0),
        emptySegmentCount = segmentRecords.Count(item => item.DurationMs <= 0)
    };

    File.WriteAllText(sessionPath, JsonSerializer.Serialize(session, JsonOptions.Indented));
    File.WriteAllText(metricsPath, JsonSerializer.Serialize(metrics, JsonOptions.Indented));
    Console.WriteLine($"Segments: {segmentRecords.Count}");
    Console.WriteLine(sessionPath);
    return 0;
}

static float[] ReadAudio(AudioFileReader reader)
{
    var floats = new List<float>();
    var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
    int read;
    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        floats.AddRange(buffer.Take(read));
    }

    return floats.ToArray();
}

static IVadEngine CreateVad(SegmentOptions options)
{
    var modelPath = FindModelPath(options.Model);
    if (modelPath is not null)
    {
        return new SileroOnnxVadEngine(modelPath, options.VadThreshold);
    }

    throw new FileNotFoundException("Silero VAD ONNX model was not found. Use --model or place it in models/silero_vad.onnx.");
}

static string? FindModelPath(string? configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
    {
        return configuredPath;
    }

    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "models", "silero_vad.onnx");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    return null;
}

[Verb("segment", HelpText = "Run the VAD and segmentation pipeline for an audio file.")]
internal sealed class SegmentOptions
{
    [Option("input", Required = true, HelpText = "Input wav/mp3/audio file path.")]
    public string Input { get; init; } = "";

    [Option("output", Required = false, Default = "data/debug-audio", HelpText = "Output diagnostics directory.")]
    public string Output { get; init; } = "data/debug-audio";

    [Option("model", Required = false, HelpText = "Optional Silero VAD ONNX model path.")]
    public string? Model { get; init; }

    [Option("vad-threshold", Required = false, Default = 0.5f)]
    public float VadThreshold { get; init; } = 0.5f;

    [Option("frame-ms", Required = false, Default = 32)]
    public int FrameMs { get; init; } = 32;

    [Option("start-speech-ms", Required = false, Default = 96)]
    public int StartSpeechMs { get; init; } = 96;

    [Option("pre-roll-ms", Required = false, Default = 192)]
    public int PreRollMs { get; init; } = 192;

    [Option("end-silence-ms", Required = false, Default = 300)]
    public int EndSilenceMs { get; init; } = 300;

    [Option("soft-break-silence-ms", Required = false, Default = 128)]
    public int SoftBreakSilenceMs { get; init; } = 128;

    [Option("min-segment-ms", Required = false, Default = 900)]
    public int MinSegmentMs { get; init; } = 900;

    [Option("soft-max-segment-ms", Required = false, Default = 4000)]
    public int SoftMaxSegmentMs { get; init; } = 4000;

    [Option("hard-max-segment-ms", Required = false, Default = 10000)]
    public int HardMaxSegmentMs { get; init; } = 10000;

    [Option("overlap-ms", Required = false, Default = 600)]
    public int OverlapMs { get; init; } = 600;
}

internal sealed record VadRecord(long TimeMs, float Probability, bool IsSpeech);

internal sealed record SegmentRecord(
    int Sequence,
    long StartMs,
    long EndMs,
    long DurationMs,
    string CutReason,
    int OverlapMs,
    string File);

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}
