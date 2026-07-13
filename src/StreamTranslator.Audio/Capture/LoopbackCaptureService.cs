using System.Diagnostics;
using NAudio.Dmo;
using NAudio.Wave;
using StreamTranslator.Audio.Segmentation;

namespace StreamTranslator.Audio.Capture;

public sealed class LoopbackCaptureService : IDisposable
{
    private readonly AudioDeviceService _deviceService;
    private readonly string _deviceId;
    private readonly bool _followDefaultDevice;
    private readonly int _frameDurationMs;
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    private WasapiLoopbackCapture? _capture;
    private PcmFrameBuffer? _frameBuffer;
    private StreamingAudioNormalizer? _normalizer;
    private SilenceGapFiller? _silenceGapFiller;
    private Timer? _silenceTimer;
    private bool _stopRequested;

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
    public event EventHandler<AudioCaptureStoppedEventArgs>? CaptureStopped;

    public void Start()
    {
        lock (_sync)
        {
            if (_capture is not null)
            {
                return;
            }

            var device = _deviceService.GetDevice(_deviceId, _followDefaultDevice);
            _capture = new WasapiLoopbackCapture(device);
            var format = _capture.WaveFormat;
            EnsureSupportedFormat(format);
            _normalizer = new StreamingAudioNormalizer(format.SampleRate, format.Channels);
            _frameBuffer = new PcmFrameBuffer(AudioNormalizer.TargetSampleRate, _frameDurationMs);
            _silenceGapFiller = new SilenceGapFiller(
                AudioNormalizer.TargetSampleRate,
                _frameDurationMs,
                triggerMs: Math.Max(128, _frameDurationMs * 4));
            _stopRequested = false;
            _clock.Restart();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _silenceTimer = new Timer(OnSilenceTimer, null, _frameDurationMs, _frameDurationMs);
            _capture.StartRecording();
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        lock (_sync)
        {
            _stopRequested = true;
            capture = _capture;
        }

        capture?.StopRecording();
        DisposeCapture();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            var frameBuffer = _frameBuffer;
            var normalizer = _normalizer;
            if (_capture is null || frameBuffer is null || normalizer is null || e.BytesRecorded == 0)
            {
                return;
            }

            _silenceGapFiller?.MarkDataReceived(_clock.ElapsedMilliseconds);
            var normalized = normalizer.ProcessFloat32Bytes(e.Buffer.AsSpan(0, e.BytesRecorded));
            EmitFrames(frameBuffer.Push(normalized));
        }
    }

    private void OnSilenceTimer(object? state)
    {
        try
        {
            lock (_sync)
            {
                if (_capture is null || _frameBuffer is null || _silenceGapFiller is null)
                {
                    return;
                }

                var sampleCount = _silenceGapFiller.GetMissingSampleCount(_clock.ElapsedMilliseconds);
                if (sampleCount > 0)
                {
                    EmitFrames(_frameBuffer.Push(new short[sampleCount]));
                }
            }
        }
        catch (Exception ex)
        {
            DisposeCapture();
            CaptureStopped?.Invoke(this, new AudioCaptureStoppedEventArgs(ex));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool expected;
        lock (_sync)
        {
            expected = _stopRequested;
        }

        DisposeCapture();
        if (!expected)
        {
            CaptureStopped?.Invoke(this, new AudioCaptureStoppedEventArgs(e.Exception));
        }
    }

    private void DisposeCapture()
    {
        lock (_sync)
        {
            _silenceTimer?.Dispose();
            _silenceTimer = null;
            _clock.Stop();
            _normalizer?.Dispose();
            _normalizer = null;
            _silenceGapFiller = null;

            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            _frameBuffer = null;
        }
    }

    private void EmitFrames(IEnumerable<PcmAudioFrame> frames)
    {
        foreach (var frame in frames)
        {
            FrameCaptured?.Invoke(this, frame);
        }
    }

    private static void EnsureSupportedFormat(WaveFormat format)
    {
        var isExtensibleFloat = format is WaveFormatExtensible extensible &&
            extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        if ((format.Encoding != WaveFormatEncoding.IeeeFloat && !isExtensibleFloat) || format.BitsPerSample != 32)
        {
            throw new NotSupportedException($"Unsupported loopback format: {format.Encoding}, {format.BitsPerSample} bits.");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
