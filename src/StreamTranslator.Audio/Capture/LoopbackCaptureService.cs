using NAudio.Wave;
using StreamTranslator.Audio.Segmentation;

namespace StreamTranslator.Audio.Capture;

public sealed class LoopbackCaptureService : IDisposable
{
    private readonly AudioDeviceService _deviceService;
    private readonly string _deviceId;
    private readonly bool _followDefaultDevice;
    private readonly int _frameDurationMs;
    private WasapiLoopbackCapture? _capture;
    private PcmFrameBuffer? _frameBuffer;

    public LoopbackCaptureService(
        AudioDeviceService deviceService,
        string deviceId,
        bool followDefaultDevice,
        int frameDurationMs = 32)
    {
        _deviceService = deviceService;
        _deviceId = deviceId;
        _followDefaultDevice = followDefaultDevice;
        _frameDurationMs = frameDurationMs;
    }

    public event EventHandler<PcmAudioFrame>? FrameCaptured;

    public void Start()
    {
        if (_capture is not null)
        {
            return;
        }

        var device = _deviceService.GetDevice(_deviceId, _followDefaultDevice);
        _capture = new WasapiLoopbackCapture(device);
        _frameBuffer = new PcmFrameBuffer(AudioNormalizer.TargetSampleRate, _frameDurationMs);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.StopRecording();
        DisposeCapture();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        var frameBuffer = _frameBuffer;
        if (capture is null || frameBuffer is null || e.BytesRecorded == 0)
        {
            return;
        }

        var format = capture.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
        {
            throw new NotSupportedException($"Unsupported loopback format: {format.Encoding}, {format.BitsPerSample} bits.");
        }

        var mono = AudioNormalizer.ConvertFloat32ToMonoPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), format.Channels);
        var normalized = AudioNormalizer.ResampleLinear(mono, format.SampleRate);
        foreach (var frame in frameBuffer.Push(normalized))
        {
            FrameCaptured?.Invoke(this, frame);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        DisposeCapture();
    }

    private void DisposeCapture()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _capture = null;
        _frameBuffer = null;
    }

    public void Dispose()
    {
        Stop();
    }
}

