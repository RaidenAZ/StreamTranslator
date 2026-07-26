using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<PcmAudioFrame> _emitQueue = new();
    private readonly object _emitLock = new();
    private readonly Stopwatch _clock = new();
    private WasapiLoopbackCapture? _capture;
    private NAudio.CoreAudioApi.MMDevice? _device;
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
            var capture = new WasapiLoopbackCapture(device);
            try
            {
                // Validate before publishing to _capture so a failed Start leaves
                // the service reusable instead of permanently "already started".
                EnsureSupportedFormat(capture.WaveFormat);
            }
            catch
            {
                capture.Dispose();
                device.Dispose();
                throw;
            }

            _device = device;
            _capture = capture;
            _normalizer = new StreamingAudioNormalizer(capture.WaveFormat.SampleRate, capture.WaveFormat.Channels);
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
            EnqueueFrames(frameBuffer.Push(normalized));
        }

        // Subscribers run VAD and segmentation; keeping them outside _sync prevents
        // them from starving the silence timer and from deadlocking against Stop().
        DrainEmitQueue();
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
                    EnqueueFrames(_frameBuffer.Push(new short[sampleCount]));
                }
            }

            DrainEmitQueue();
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
        WasapiLoopbackCapture? capture;
        NAudio.CoreAudioApi.MMDevice? device;
        Timer? silenceTimer;
        StreamingAudioNormalizer? normalizer;
        lock (_sync)
        {
            silenceTimer = _silenceTimer;
            _silenceTimer = null;
            _clock.Stop();
            normalizer = _normalizer;
            _normalizer = null;
            _silenceGapFiller = null;

            capture = _capture;
            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                _capture = null;
            }

            device = _device;
            _device = null;
            _frameBuffer = null;
        }

        // WasapiCapture.Dispose joins the capture thread, which may itself be
        // waiting on _sync in OnDataAvailable; disposing outside the lock breaks
        // that deadlock cycle. Device must be released after capture.
        silenceTimer?.Dispose();
        normalizer?.Dispose();
        capture?.Dispose();
        device?.Dispose();
    }

    private void EnqueueFrames(IReadOnlyList<PcmAudioFrame> frames)
    {
        // Callers hold _sync, so queue order matches frame timestamp order.
        foreach (var frame in frames)
        {
            _emitQueue.Enqueue(frame);
        }
    }

    private void DrainEmitQueue()
    {
        // Downstream (stateful VAD, segmenter) must stay single-threaded and
        // ordered: frames are enqueued under _sync and drained by one thread at
        // a time. A thread that loses TryEnter leaves its frames to the current
        // drainer; the outer loop re-checks after release so nothing is stranded.
        while (!_emitQueue.IsEmpty && Monitor.TryEnter(_emitLock))
        {
            try
            {
                while (_emitQueue.TryDequeue(out var frame))
                {
                    FrameCaptured?.Invoke(this, frame);
                }
            }
            finally
            {
                Monitor.Exit(_emitLock);
            }
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
