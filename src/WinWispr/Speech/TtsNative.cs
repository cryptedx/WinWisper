using System.Runtime.InteropServices;
using SherpaOnnx;

namespace WinWispr.Speech;

internal sealed record TtsAudio(float[] Samples, int SampleRate);
internal sealed record TtsEngine(Action Unload, Func<string, float, TtsAudio> Generate);

internal static class TtsNative
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _phonemizerFingerprint;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetStatus();

    internal static TtsEngine Load(string directory)
    {
        TtsVoice voice = TtsVoice.Read(directory);
        lock (Gate)
        {
            if (_loaded) throw new InvalidOperationException("Unload the active voice before loading another.");
            // Upstream initializes eSpeak once per process. Reject different data rather
            // than silently use the first voice's phonemizer files for a later voice.
            string fingerprint = string.Join("\n", voice.Files
                .Where(f => f.Path.StartsWith(voice.DataDirectory + "/", StringComparison.Ordinal))
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .Select(f => f.Path[(voice.DataDirectory.Length + 1)..] + ":" + f.Sha256.ToLowerInvariant()));
            if (_phonemizerFingerprint is not null && _phonemizerFingerprint != fingerprint)
                throw new InvalidOperationException("Voices must use identical phonemizer data; restart to change that data.");
            nint library = SpeechRuntime.InstallNativeResolver();
            var status = Marshal.GetDelegateForFunctionPointer<GetStatus>(
                NativeLibrary.GetExport(library, "WinWisprGetLastTtsStatus"));
            var config = new OfflineTtsConfig();
            config.Model.Provider = "cpu";
            config.Model.NumThreads = 2;
            config.Model.Vits.Model = TtsVoice.Resolve(directory, voice.Model);
            config.Model.Vits.Tokens = TtsVoice.Resolve(directory, voice.Tokens);
            config.Model.Vits.DataDir = TtsVoice.Resolve(directory, voice.DataDirectory);
            _phonemizerFingerprint = fingerprint;
            var tts = new OfflineTts(config);
            if (status() != 1)
            {
                tts.Dispose();
                throw new InvalidOperationException("The local voice could not be loaded.");
            }
            _loaded = true;
            return new TtsEngine(() => { lock (Gate) { tts.Dispose(); _loaded = false; } }, (text, speed) =>
            {
                lock (Gate)
                {
                    OfflineTtsGeneratedAudio audio = tts.Generate(text, speed, 0);
                    try
                    {
                        if (audio.Handle == IntPtr.Zero)
                            throw new InvalidOperationException("Local voice synthesis failed.");
                        if (audio.SampleRate is < 8000 or > 48000 || audio.NumSamples <= 0
                            || audio.NumSamples > audio.SampleRate * 60)
                            throw new InvalidDataException("Voice output exceeds the prototype audio limit.");
                        return new TtsAudio(audio.Samples, audio.SampleRate);
                    }
                    finally { audio.Dispose(); }
                }
            });
        }
    }
}
