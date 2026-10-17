using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SherpaOnnx;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

/// <summary>
/// Short German dictations through the real Parakeet model (issue #9). The offline Piper voices speak one- and two-word
/// phrases; the test compares the plain model result with the product recognizer and requires that the product
/// recognizer keeps clearly more German phrases German without losing English ones.
/// WINWISPR_EXTRA_VOICES may name a folder of further Piper voice folders (name contains de_DE or en_) for a broader
/// measurement; they use the bundled German voice's eSpeak data.
/// </summary>
[TestClass]
[TestCategory("WindowsAcceptance")]
public sealed class ShortDictationLanguageAcceptanceTests
{
    private const int Rate = ShortDictationLanguage.SampleRate;

    private static readonly string[] German =
    [
        "Hallo", "Danke", "Danke schön", "Ja genau", "Nein", "Guten Morgen", "Bis morgen", "Alles klar", "Vielleicht",
        "Auf jeden Fall", "Nächste Woche", "Gute Nacht", "Bitte", "Tschüss", "Natürlich", "Keine Ahnung", "Stimmt",
        "Mach weiter", "Sehr gut", "Wie geht's", "Termin", "Einkaufsliste", "Morgen früh", "Ich komme", "Sofort",
    ];

    private static readonly string[] English =
    [
        "Hello", "Thank you", "Yes exactly", "Good morning", "See you tomorrow", "All right", "Maybe", "Next week",
        "Sounds good", "No idea", "Of course", "Please", "Meeting", "Right now", "I'm coming",
    ];

    [TestMethod]
    public void Short_german_dictations_stay_german()
    {
        string modelDirectory = TrustedSpeechFixtures.Require().ModelDirectory;
        string? voiceRoot = Environment.GetEnvironmentVariable("WINWISPR_TTS_VOICES") ?? FindVoices();
        if (voiceRoot is null || !Directory.Exists(voiceRoot))
            Assert.Inconclusive("Short dictation acceptance needs the offline voices (artifacts/voices or WINWISPR_TTS_VOICES).");

        var voices = Directory.GetDirectories(voiceRoot).ToList();
        string german = voices.First(voice => TtsVoice.Read(voice).Language == "de-DE");
        string espeak = Path.Combine(german, "espeak-ng-data");
        string? extra = Environment.GetEnvironmentVariable("WINWISPR_EXTRA_VOICES");
        if (!string.IsNullOrWhiteSpace(extra))
            voices.AddRange(Directory.GetDirectories(extra));

        SpeechRuntime.InstallNativeResolver();
        using OfflineRecognizer recognizer = Parakeet(modelDirectory);
        string lastDecoded = string.Empty;
        string Decode(float[] samples)
        {
            using OfflineStream stream = recognizer.CreateStream();
            stream.AcceptWaveform(Rate, samples);
            recognizer.Decode(stream);
            return lastDecoded = stream.Result.Text.Trim();
        }
        var product = new ShortDictationLanguage(Decode);

        var score = new Dictionary<string, int[]>();
        var report = new StringBuilder();
        foreach (string voice in voices)
        {
            string name = Path.GetFileName(voice);
            string? language = name.Contains("de_DE", StringComparison.Ordinal) ? "de"
                : name.Contains("en_", StringComparison.Ordinal) ? "en" : null;
            if (language is null)
                continue;
            using OfflineTts tts = Tts(voice, espeak);
            foreach (string phrase in language == "de" ? German : English)
            foreach (float speed in new[] { 0.9f, 1.15f })
            {
                float[] clip = Synthesize(tts, phrase, speed);
                string plain = Decode(clip);
                string fixedText = product.Recognize(clip);
                bool plainHit = Same(plain, phrase), fixedHit = Same(fixedText, phrase);
                foreach (string key in new[] { language, language + "/" + name })
                {
                    int[] counts = score.TryGetValue(key, out int[]? existing) ? existing : score[key] = new int[3];
                    counts[0] += plainHit ? 1 : 0;
                    counts[1] += fixedHit ? 1 : 0;
                    counts[2]++;
                }
                if (plainHit != fixedHit)
                    report.AppendLine(CultureInfo.InvariantCulture, $"{name} {phrase} x{speed}: '{plain}' -> '{fixedText}' (primed: '{lastDecoded}')");
            }
        }

        foreach ((string key, int[] counts) in score.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            report.AppendLine(CultureInfo.InvariantCulture, $"{key}: plain {counts[0]}/{counts[2]}, product {counts[1]}/{counts[2]}");
        Console.WriteLine(report.ToString());

        // Measured with the bundled voices: German 35 -> 48 of 50, English unchanged. The bundled German voice also
        // speaks the primer, so WINWISPR_EXTRA_VOICES gives the fairer figure (six further German voices: 131 -> 185
        // of 300). The bounds leave room for runtime differences without accepting a fix that no longer works.
        int[] de = score["de"], en = score["en"];
        Assert.IsTrue(de[1] >= de[0] + de[2] / 10, $"German: plain {de[0]}, product {de[1]} of {de[2]}.");
        Assert.IsTrue(en[1] >= en[0] - en[2] / 10, $"English: plain {en[0]}, product {en[1]} of {en[2]}.");
    }

    // Same words: case, punctuation and spaces ("Dankeschön") do not count.
    private static bool Same(string actual, string expected) => Letters(actual) == Letters(expected);

    private static string Letters(string text) =>
        new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static OfflineRecognizer Parakeet(string directory) => new(new OfflineRecognizerConfig
    {
        FeatConfig = new FeatureConfig { SampleRate = Rate, FeatureDim = 80 },
        ModelConfig = new OfflineModelConfig
        {
            Tokens = Path.Combine(directory, "tokens.txt"),
            NumThreads = 4,
            Provider = "cpu",
            ModelType = "nemo_transducer",
            Transducer = new OfflineTransducerModelConfig
            {
                Encoder = Path.Combine(directory, "encoder.int8.onnx"),
                Decoder = Path.Combine(directory, "decoder.int8.onnx"),
                Joiner = Path.Combine(directory, "joiner.int8.onnx"),
            },
        },
        DecodingMethod = "greedy_search",
    });

    // eSpeak initializes once per process, so every voice uses the same phonemizer data.
    private static OfflineTts Tts(string directory, string espeak)
    {
        var config = new OfflineTtsConfig();
        config.Model.Provider = "cpu";
        config.Model.NumThreads = 2;
        config.Model.Vits.Model = Directory.GetFiles(directory, "*.onnx").Single();
        config.Model.Vits.Tokens = Path.Combine(directory, "tokens.txt");
        config.Model.Vits.DataDir = espeak;
        return new OfflineTts(config);
    }

    private static float[] Synthesize(OfflineTts tts, string text, float speed)
    {
        OfflineTtsGeneratedAudio audio = tts.Generate(text, speed, 0);
        float[] samples = audio.Samples;
        if (audio.SampleRate == Rate)
            return samples;
        // Linear resampling to 16 kHz.
        var output = new float[(int)((long)samples.Length * Rate / audio.SampleRate)];
        for (int index = 0; index < output.Length; index++)
        {
            double position = index * (double)audio.SampleRate / Rate;
            int left = (int)position;
            int right = Math.Min(left + 1, samples.Length - 1);
            double fraction = position - left;
            output[index] = (float)(samples[left] * (1 - fraction) + samples[right] * fraction);
        }
        return output;
    }

    private static string? FindVoices()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "artifacts", "voices");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
