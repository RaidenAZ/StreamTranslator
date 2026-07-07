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
}

