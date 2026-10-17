using System.Buffers.Binary;
using System.Text;
using WinWispr.Translation;

namespace WinWispr.Speech;

/// <summary>
/// Keeps one- and two-word German dictations German. Parakeet has no language input and picks the language from the
/// audio alone, so a clip of one or two German words often comes out as English or Dutch ("Hallo" as "Hello",
/// "Nein" as "Nine"). A short result that is not already German is decoded again with a spoken German phrase
/// ("Also ich sage") in front of the clip; the phrase gives the encoder German context, and its own transcript is
/// removed from the front of the result. When that removal does not match, or leaves no words, the first result stays.
/// Longer dictations carry enough context of their own and are never decoded twice.
/// </summary>
internal sealed class ShortDictationLanguage
{
    internal const int SampleRate = 16_000;

    /// <summary>Results with more words than this keep the first decoding.</summary>
    internal const int MaxWords = 3;

    /// <summary>Clips longer than this keep the first decoding.</summary>
    internal const int MaxSamples = 4 * SampleRate;

    // Silence between the phrase and the clip. Measured: 0.4 s keeps the phrase transcript intact, while shorter gaps
    // let the phrase merge into the clip and a phrase that ends a sentence makes the model drop the clip.
    private const int GapSamples = 2 * SampleRate / 5;

    private static readonly Lazy<float[]> PrimerSamples = new(ReadPrimer);
    private readonly Func<float[], string> _decode;
    private readonly Lazy<string> _primerText;

    /// <param name="decode">Decodes one complete 16 kHz mono waveform and returns its trimmed transcript.</param>
    internal ShortDictationLanguage(Func<float[], string> decode)
    {
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        // The model's own transcript of the phrase, so the removal matches its spelling and punctuation.
        _primerText = new Lazy<string>(() => _decode(Primer).Trim().TrimEnd('.', ',', '!', '?'));
    }

    /// <summary>The spoken German phrase, 16 kHz mono.</summary>
    internal static float[] Primer => PrimerSamples.Value;

    internal string Recognize(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        string first = _decode(samples);
        if (samples.Length > MaxSamples || CountWords(first) > MaxWords
            || LanguageDetector.Detect(first) == DetectedLanguage.German)
        {
            return first;
        }

        string primerText = _primerText.Value;
        if (primerText.Length == 0)
            return first;

        float[] primed = new float[Primer.Length + GapSamples + samples.Length];
        Primer.CopyTo(primed, 0);
        samples.CopyTo(primed, Primer.Length + GapSamples);
        string second = _decode(primed);
        if (!second.StartsWith(primerText, StringComparison.OrdinalIgnoreCase))
            return first;

        string rest = second[primerText.Length..].TrimStart('.', ',', '!', '?', ' ');
        if (!rest.Any(char.IsLetterOrDigit))
            return first;

        // The phrase can swallow the clip's first word ("Yes, exactly" as "Also ich sage exactly"); a result that is
        // only the end of the first one lost words rather than changed language.
        string[] firstWords = Words(first), restWords = Words(rest);
        return restWords.Length < firstWords.Length && firstWords[^restWords.Length..].SequenceEqual(restWords)
            ? first
            : rest;
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string[] Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(word => word.Length > 0)
            .ToArray();

    // A 16-bit PCM mono 16 kHz WAVE file, embedded in the assembly.
    private static float[] ReadPrimer()
    {
        using Stream stream = typeof(ShortDictationLanguage).Assembly.GetManifestResourceStream("WinWispr.Speech.GermanPrimer.wav")
            ?? throw new InvalidOperationException("The German primer resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (bytes.Length < 44
            || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE"
            || Encoding.ASCII.GetString(bytes, 12, 4) != "fmt "
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4)) != SampleRate
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2)) != 16
            || Encoding.ASCII.GetString(bytes, 36, 4) != "data")
        {
            throw new InvalidDataException("The German primer must be a 16-bit PCM mono 16 kHz WAVE file.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
        if (length <= 0 || length % 2 != 0 || 44 + length > bytes.Length)
            throw new InvalidDataException("The German primer data chunk is invalid.");

        var samples = new float[length / 2];
        for (int index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + index * 2, 2)) / 32_768f;
        return samples;
    }
}
