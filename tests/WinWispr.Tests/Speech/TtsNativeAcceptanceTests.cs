using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SherpaOnnx;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

[TestClass]
[TestCategory("WindowsAcceptance")]
public sealed class TtsNativeAcceptanceTests
{
    [TestMethod]
    public void Invalid_native_voice_is_rejected_without_crossing_the_abi()
    {
        nint first = SpeechRuntime.InstallNativeResolver();
        Assert.AreEqual(first, SpeechRuntime.InstallNativeResolver());
        Assert.IsTrue(NativeLibrary.TryGetExport(first, "WinWisprGetLastTtsStatus", out _));
        using var invalid = new OfflineTts(new OfflineTtsConfig());
        var getStatus = Marshal.GetDelegateForFunctionPointer<Status>(NativeLibrary.GetExport(first, "WinWisprGetLastTtsStatus"));
        Assert.AreEqual(0, getStatus());
    }

    [TestMethod]
    public void Corrupt_native_model_returns_failure_without_terminating_process()
    {
        nint library = SpeechRuntime.InstallNativeResolver();
        string directory = Path.Combine(Path.GetTempPath(), "WinWispr-invalid-tts-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "model.onnx"), "not an ONNX model");
            File.WriteAllText(Path.Combine(directory, "tokens.txt"), "_ 0");
            var config = new OfflineTtsConfig();
            config.Model.Provider = "cpu";
            config.Model.Vits.Model = Path.Combine(directory, "model.onnx");
            config.Model.Vits.Tokens = Path.Combine(directory, "tokens.txt");
            // Empty DataDir passes file-level validation; ONNX construction runs
            // before frontend initialization and must reject these corrupt bytes.
            config.Model.Vits.DataDir = "";
            using var invalid = new OfflineTts(config);
            var getStatus = Marshal.GetDelegateForFunctionPointer<Status>(NativeLibrary.GetExport(library, "WinWisprGetLastTtsStatus"));
            Assert.AreEqual(0, getStatus());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public Task Supplied_voices_tts_first_then_stt() => BenchmarkAsync(sttFirst: false);

    [TestMethod]
    public Task Supplied_voices_stt_first_then_tts() => BenchmarkAsync(sttFirst: true);

    private static async Task BenchmarkAsync(bool sttFirst)
    {
        string? voiceRoot = Environment.GetEnvironmentVariable("WINWISPR_TTS_VOICES");
        string? stt = Environment.GetEnvironmentVariable("WINWISPR_TTS_STT_MODEL")
            ?? ModelInstaller.CreateDefault().GetActiveModelDirectory();
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "WinWispr.sln"))) repository = repository.Parent;
        voiceRoot ??= repository is null ? null : Path.Combine(repository.FullName, "artifacts", "voices");
        if (string.IsNullOrWhiteSpace(voiceRoot) || !Directory.Exists(voiceRoot) || string.IsNullOrWhiteSpace(stt))
            Assert.Inconclusive("Supply local WINWISPR_TTS_VOICES and WINWISPR_TTS_STT_MODEL; no downloads are permitted.");
        string staged = Path.Combine(Path.GetTempPath(), "WinWispr-tts-acceptance-" + Guid.NewGuid());
        try
        {
            // Model reads and synthesis should exercise a local installation, not SMB latency.
            foreach (string source in Directory.EnumerateFiles(voiceRoot!, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(staged, Path.GetRelativePath(voiceRoot!, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            string[] voices = Directory.GetDirectories(staged);
            Assert.IsTrue(voices.Length >= 2, "Supply German and English voices.");
            var languages = voices.Select(v => TtsVoice.Read(v).Language).ToArray();
            Assert.Contains("de-DE", languages);
            Assert.IsTrue(languages.Any(l => l.StartsWith("en-", StringComparison.Ordinal)));
            {
                SpeechRuntime? speech = sttFirst ? SpeechRuntime.LoadForAcceptance(stt!, SpeechBackend.Cpu) : null;
                try
                {
                    foreach (string directory in voices)
                    {
                        TtsEngine? engine = null;
                        await TtsPrototype.MeasureAsync("voice-load", () => Task.Run(() => engine = TtsNative.Load(directory)));
                        try
                        {
                            speech ??= SpeechRuntime.LoadForAcceptance(stt!, SpeechBackend.Cpu);
                            string language = languages[Array.IndexOf(voices, directory)];
                            foreach (float speed in new[] { 0.8f, 1f, 1.25f })
                            {
                                string text = language == "de-DE" ? TtsPrototype.German : TtsPrototype.English;
                                double seconds = 0;
                                Stopwatch timer = Stopwatch.StartNew();
                                await TtsPrototype.MeasureAsync("synthesis", () => Task.Run(() =>
                                {
                                    foreach (string section in TtsSession.Sections(text))
                                    {
                                        TtsAudio audio = engine!.Generate(section, speed);
                                        seconds += audio.Samples.Length / (double)audio.SampleRate;
                                    }
                                }));
                                Console.WriteLine($"Speed={speed}; AudioSeconds={seconds:F3}; RTF={timer.Elapsed.TotalSeconds / seconds:F3}");
                                Assert.IsTrue(seconds > 0);
                            }
                        }
                        finally { if (engine is not null) await TtsPrototype.MeasureAsync("voice-unload", () => Task.Run(engine.Unload)); }
                    }
                }
                finally { speech?.Dispose(); }
            }
        }
        finally { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Status();
}
