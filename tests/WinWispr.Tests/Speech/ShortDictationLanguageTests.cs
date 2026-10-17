using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class ShortDictationLanguageTests
{
    private const string PrimerText = "Also ich sage";

    [TestMethod]
    public void Embedded_primer_is_a_short_spoken_phrase()
    {
        float[] primer = ShortDictationLanguage.Primer;

        Assert.IsTrue(primer.Length is > ShortDictationLanguage.SampleRate / 2 and < 2 * ShortDictationLanguage.SampleRate);
        Assert.IsTrue(primer.Max(MathF.Abs) > 0.1f);
    }

    [TestMethod]
    public void German_result_is_decoded_once()
    {
        var decoder = new FakeDecoder("Danke schön.", "Also ich sage. Danke schön.");

        Assert.AreEqual("Danke schön.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(0.6)));
        Assert.AreEqual(1, decoder.ClipDecodes);
    }

    [TestMethod]
    public void Short_english_looking_result_is_replaced_by_the_primed_result()
    {
        var decoder = new FakeDecoder("Hello.", "Also ich sage, Hallo.");

        Assert.AreEqual("Hallo.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(0.6)));
        Assert.AreEqual(2, decoder.ClipDecodes);
    }

    [TestMethod]
    public void Ambiguous_result_is_decoded_again()
    {
        var decoder = new FakeDecoder("Nine.", "Also ich sage nein.");

        Assert.AreEqual("nein.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(0.6)));
    }

    [TestMethod]
    public void Primed_result_without_the_primer_transcript_keeps_the_first_result()
    {
        var decoder = new FakeDecoder("Shoes.", "Also ich sag Tschüss.");

        Assert.AreEqual("Shoes.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(0.6)));
    }

    [TestMethod]
    public void Primed_result_without_words_after_the_primer_keeps_the_first_result()
    {
        var decoder = new FakeDecoder("Yeah.", "Also ich sage.");

        Assert.AreEqual("Yeah.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(0.6)));
    }

    [TestMethod]
    public void Primed_result_that_lost_the_first_word_keeps_the_first_result()
    {
        var decoder = new FakeDecoder("Yes, exactly.", "Also ich sage exactly.");

        Assert.AreEqual("Yes, exactly.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(0.6)));
    }

    [TestMethod]
    public void Primed_result_with_fewer_but_different_words_is_used()
    {
        var decoder = new FakeDecoder("Good chair.", "Also ich sage bitte.");

        Assert.AreEqual("bitte.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(0.6)));
    }

    [TestMethod]
    public void Result_with_more_than_three_words_is_decoded_once()
    {
        var decoder = new FakeDecoder("See you next week then.", "Also ich sage bis nächste Woche dann.");

        Assert.AreEqual("See you next week then.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(1.5)));
        Assert.AreEqual(1, decoder.ClipDecodes);
    }

    [TestMethod]
    public void Clip_longer_than_four_seconds_is_decoded_once()
    {
        var decoder = new FakeDecoder("Okay.", "Also ich sage okay.");

        Assert.AreEqual("Okay.", new ShortDictationLanguage(decoder.Decode).Recognize(Clip(4.5)));
        Assert.AreEqual(1, decoder.ClipDecodes);
    }

    [TestMethod]
    public void Primer_transcript_is_decoded_once_per_recognizer()
    {
        var decoder = new FakeDecoder("Hello.", "Also ich sage Hallo.");
        var language = new ShortDictationLanguage(decoder.Decode);

        language.Recognize(Clip(0.6));
        language.Recognize(Clip(0.6));

        Assert.AreEqual(1, decoder.PrimerDecodes);
    }

    [TestMethod]
    public void Primed_audio_is_the_primer_a_gap_and_the_clip()
    {
        var decoder = new FakeDecoder("Hello.", "Also ich sage Hallo.");
        float[] clip = Clip(0.6);

        new ShortDictationLanguage(decoder.Decode).Recognize(clip);

        float[] primed = decoder.Primed!;
        float[] primer = ShortDictationLanguage.Primer;
        int gap = primed.Length - primer.Length - clip.Length;
        Assert.AreEqual(ShortDictationLanguage.SampleRate * 2 / 5, gap);
        CollectionAssert.AreEqual(primer, primed[..primer.Length]);
        Assert.IsTrue(primed[primer.Length..(primer.Length + gap)].All(sample => sample == 0f));
        CollectionAssert.AreEqual(clip, primed[(primer.Length + gap)..]);
    }

    private static float[] Clip(double seconds) =>
        Enumerable.Repeat(0.25f, (int)(seconds * ShortDictationLanguage.SampleRate)).ToArray();

    /// <summary>Answers the primer alone, the clip alone, and the primer in front of the clip.</summary>
    private sealed class FakeDecoder(string clipText, string primedText)
    {
        internal int ClipDecodes { get; private set; }
        internal int PrimerDecodes { get; private set; }
        internal float[]? Primed { get; private set; }

        internal string Decode(float[] samples)
        {
            float[] primer = ShortDictationLanguage.Primer;
            if (samples.Length == primer.Length && samples.AsSpan().SequenceEqual(primer))
            {
                PrimerDecodes++;
                return PrimerText + ".";
            }

            ClipDecodes++;
            if (samples.Length > primer.Length && samples.AsSpan(0, primer.Length).SequenceEqual(primer))
            {
                Primed = samples;
                return primedText;
            }

            return clipText;
        }
    }
}
