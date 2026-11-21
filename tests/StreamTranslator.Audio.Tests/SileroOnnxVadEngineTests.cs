using StreamTranslator.Audio.Vad;

namespace StreamTranslator.Audio.Tests;

[TestClass]
public sealed class SileroOnnxVadEngineTests
{
    [TestMethod]
    public void Analyze_ReturnsProbabilityWhenModelExists()
    {
        var modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "silero_vad.onnx"));
        if (!File.Exists(modelPath))
        {
            Assert.Inconclusive("Silero VAD ONNX model is not present.");
        }

        using var engine = new SileroOnnxVadEngine(modelPath);
        var decision = engine.Analyze(new short[512], 16000);

        Assert.IsTrue(decision.Probability >= 0);
        Assert.IsTrue(decision.Probability <= 1);
    }

    [TestMethod]
    public void Reset_RestoresInitialModelState()
    {
        var modelPath = ModelPath();
        if (!File.Exists(modelPath))
        {
            Assert.Inconclusive("Silero VAD ONNX model is not present.");
        }

        using var engine = new SileroOnnxVadEngine(modelPath);
        var initial = engine.Analyze(new short[512], 16000).Probability;
        engine.Analyze(Enumerable.Repeat<short>(1000, 512).ToArray(), 16000);
        engine.Reset();
        var afterReset = engine.Analyze(new short[512], 16000).Probability;

        Assert.AreEqual(initial, afterReset, 0.000001f);
    }

    [TestMethod]
    public void Analyze_RejectsFrameLengthNotSupportedBySilero()
    {
        var modelPath = ModelPath();
        if (!File.Exists(modelPath))
        {
            Assert.Inconclusive("Silero VAD ONNX model is not present.");
        }

        using var engine = new SileroOnnxVadEngine(modelPath);
        Assert.ThrowsException<ArgumentException>(() => engine.Analyze(new short[160], 16000));
    }

    [TestMethod]
    public void EvaluateHysteresis_HoldsSpeechAboveExitThresholdAndExitsBelow()
    {
        // Inactive: requires enterThreshold (0.50) to enter speech.
        Assert.IsFalse(SileroOnnxVadEngine.EvaluateHysteresis(false, 0.45f, 0.50f, 0.34f));
        Assert.IsTrue(SileroOnnxVadEngine.EvaluateHysteresis(false, 0.50f, 0.50f, 0.34f));

        // Active: holds speech across soft endings [0.34, 0.50), exits only below 0.34.
        Assert.IsTrue(SileroOnnxVadEngine.EvaluateHysteresis(true, 0.38f, 0.50f, 0.34f));
        Assert.IsTrue(SileroOnnxVadEngine.EvaluateHysteresis(true, 0.34f, 0.50f, 0.34f));
        Assert.IsFalse(SileroOnnxVadEngine.EvaluateHysteresis(true, 0.33f, 0.50f, 0.34f));
    }

    private static string ModelPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "silero_vad.onnx"));
    }
}
