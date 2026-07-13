using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace StreamTranslator.Audio.Vad;

public sealed class SileroOnnxVadEngine : IVadEngine
{
    private readonly InferenceSession _session;
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _context = new float[64];
    private readonly float _threshold;
    private int _lastSampleRate;

    public SileroOnnxVadEngine(string modelPath, float threshold = 0.5f)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Silero VAD ONNX model was not found.", modelPath);
        }

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1
        };

        _session = new InferenceSession(modelPath, options);
        _threshold = threshold;
    }

    public VadDecision Analyze(ReadOnlySpan<short> pcm16Frame, int sampleRate)
    {
        if (sampleRate is not (8000 or 16000))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Silero VAD expects 8kHz or 16kHz audio.");
        }

        var expectedFrameLength = sampleRate == 16000 ? 512 : 256;
        if (pcm16Frame.Length != expectedFrameLength)
        {
            throw new ArgumentException(
                $"Silero VAD expects {expectedFrameLength} samples at {sampleRate} Hz.",
                nameof(pcm16Frame));
        }

        if (_lastSampleRate != 0 && _lastSampleRate != sampleRate)
        {
            Reset();
        }

        var contextSize = sampleRate == 16000 ? 64 : 32;
        var input = new float[contextSize + pcm16Frame.Length];
        Array.Copy(_context, input, contextSize);
        for (var i = 0; i < pcm16Frame.Length; i++)
        {
            input[contextSize + i] = pcm16Frame[i] / 32768f;
        }

        var inputTensor = new DenseTensor<float>(input, new[] { 1, input.Length });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        var sampleRateTensor = new DenseTensor<long>(new long[] { sampleRate }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", sampleRateTensor)
        };

        using var results = _session.Run(inputs);
        var probability = ReadSpeechProbability(results);
        UpdateState(results);
        Array.Copy(input, input.Length - contextSize, _context, 0, contextSize);
        _lastSampleRate = sampleRate;

        return new VadDecision(probability >= _threshold, probability);
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_context);
        _lastSampleRate = 0;
    }

    private static float ReadSpeechProbability(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var output = results.FirstOrDefault(result => result.Name == "output") ?? results.First();
        var tensor = output.AsTensor<float>();
        return tensor.FirstOrDefault();
    }

    private void UpdateState(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var stateOutput = results.FirstOrDefault(result => result.Name is "stateN" or "state");
        if (stateOutput is null)
        {
            return;
        }

        var tensor = stateOutput.AsTensor<float>();
        var index = 0;
        foreach (var value in tensor)
        {
            if (index >= _state.Length)
            {
                break;
            }

            _state[index++] = value;
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
