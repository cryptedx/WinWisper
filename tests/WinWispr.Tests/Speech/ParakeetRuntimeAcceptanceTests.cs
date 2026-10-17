using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class ParakeetRuntimeAcceptanceTests
{
    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    [DataRow(SpeechBackend.Cpu)]
    public void Exact_parakeet_model_returns_non_empty_text(SpeechBackend backend)
    {
        string modelDirectory = ModelDirectory();
        float[] samples = Pcm16Wave.ReadMono16Khz(SmokeWav());

        using SpeechRuntime runtime = SpeechRuntime.LoadForAcceptance(modelDirectory, backend);
        string text = runtime.Recognize(samples);

        Assert.AreEqual(backend, runtime.Backend);
        Assert.IsFalse(string.IsNullOrWhiteSpace(text));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    [DataRow(2)] // NVIDIA
    [DataRow(3)] // Intel
    public async Task Selected_gpu_transcribes_on_real_hardware(int modeValue)
    {
        var mode = (SpeechMode)modeValue;
        SpeechAdapter? adapter = SpeechAdapter.Select(mode, SpeechDevices.Enumerate());
        if (adapter is null)
            Assert.Inconclusive($"No supported {mode} GPU; this is not GPU acceptance evidence.");
        string modelDirectory = ModelDirectory();
        float[] samples = Pcm16Wave.ReadMono16Khz(SmokeWav());
        using SpeechRuntime runtime = SpeechRuntime.CreateNative(_ => modelDirectory, mode);
        await runtime.LoadInstalledAsync(CancellationToken.None);
        Assert.AreEqual(SpeechBackend.DirectMl, runtime.Backend, runtime.StatusText);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string text = await runtime.RecognizeAsync(samples, CancellationToken.None);
        Assert.IsFalse(string.IsNullOrWhiteSpace(text));
        Console.WriteLine($"{runtime.StatusText}; transcription milliseconds: {watch.ElapsedMilliseconds}");
    }

    // WINWISPR_MODEL_DIR and WINWISPR_SMOKE_WAV override the fixtures from the pinned model archive.
    private static string ModelDirectory() =>
        EnvironmentPath("WINWISPR_MODEL_DIR") ?? TrustedSpeechFixtures.Require().ModelDirectory;

    private static string SmokeWav() =>
        EnvironmentPath("WINWISPR_SMOKE_WAV") ?? TrustedSpeechFixtures.Require().GermanClip;

    private static string? EnvironmentPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        Assert.IsTrue(File.Exists(value) || Directory.Exists(value), $"{name} does not exist.");
        return Path.GetFullPath(value);
    }
}
