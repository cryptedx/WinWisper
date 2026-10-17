namespace WinWispr.Translation;

/// <summary>The language pair of one translation. The source language decides which model translates.</summary>
internal enum TranslationDirection
{
    GermanToEnglish,
    EnglishToGerman
}

/// <summary>
/// The outcome of one translation. A caller handles each kind explicitly: the module never throws for a missing,
/// incomplete, unreadable or failing model. The only exception is the caller's own cancellation. Callers keep a
/// default branch, because later tickets may add kinds.
/// </summary>
internal abstract record TranslationResult
{
    private TranslationResult()
    {
    }

    /// <summary>
    /// The text in the target language. <paramref name="Direction"/> is the direction that translated it, or null
    /// when no translation ran: the text is empty, has no letters, or is already English and was asked for in
    /// English. <paramref name="Unchanged"/> says that the text is the input: it was already in the target language
    /// or has no letters, or the model returned an ambiguous input such as a name unchanged.
    /// </summary>
    internal sealed record Translated(string Text, TranslationDirection? Direction = null, bool Unchanged = false)
        : TranslationResult;

    /// <summary>
    /// A selection is longer than <paramref name="Limit"/> characters, the most that translates within the
    /// three-second requirement. Nothing was loaded or translated.
    /// </summary>
    internal sealed record TooLong(int Limit) : TranslationResult;

    /// <summary>
    /// The translation did not finish within <paramref name="Deadline"/> and was stopped. The deadline starts when
    /// the model is loaded and the request's turn has come, so neither a load nor a queue counts against it.
    /// </summary>
    internal sealed record TimedOut(TimeSpan Deadline) : TranslationResult;

    /// <summary>
    /// The translation model is not installed, or its files are incomplete. Nothing was loaded; restoring the
    /// model's files (by running Setup) and asking again works.
    /// </summary>
    internal sealed record NotInstalled : TranslationResult;

    /// <summary>
    /// The model is installed but loading or running it failed. <paramref name="Reason"/> is a technical
    /// description that never contains the translated text. A later request retries the load.
    /// </summary>
    internal sealed record Failed(string Reason) : TranslationResult;
}

/// <summary>Why a text is translated: a selection is held to the length limit, a dictation never is.</summary>
internal enum TranslationUse
{
    /// <summary>Text the user selected or copied. It has a length limit and a short safety deadline.</summary>
    Selection,

    /// <summary>A dictation transcript. Spoken content is never dropped, so there is no length limit.</summary>
    Dictation
}

/// <summary>The language a text is written in, as far as the detector can tell.</summary>
internal enum DetectedLanguage
{
    German,
    English,

    /// <summary>No evidence, a tie, or too little lead: the text is translated from German.</summary>
    Ambiguous,

    /// <summary>No letters at all (numbers, identifiers, punctuation, emoji): there is nothing to translate.</summary>
    NoLetters
}

internal static class DetectedLanguageExtensions
{
    /// <summary>
    /// The direction that translates text of this language into the other language, so that a caller can start the
    /// right load early. Ambiguous text goes from German to English; text without letters needs no model (null).
    /// </summary>
    internal static TranslationDirection? ToDirection(this DetectedLanguage language) => language switch
    {
        DetectedLanguage.English => Translation.TranslationDirection.EnglishToGerman,
        DetectedLanguage.German or DetectedLanguage.Ambiguous => Translation.TranslationDirection.GermanToEnglish,
        _ => null
    };
}

/// <summary>
/// The limits of one translator, injectable so that tests can use short ones. The selection limit comes from the
/// measurement of ticket 05 (QUALIFICATION.md section 0, docs/testing/windows-11-acceptance.md).
/// </summary>
internal sealed record TranslationLimits
{
    /// <summary>The longest selection, in characters, that translates within three seconds on the build host.</summary>
    internal int SelectionMaxCharacters { get; init; } = DefaultSelectionMaxCharacters;

    /// <summary>The safety deadline of a selection translation (about 5 s).</summary>
    internal TimeSpan SelectionDeadline { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The safety deadline of a dictation translation for a number of source tokens:
    /// <c>min(90 s, 3 s + 0.06 s per token)</c>.
    /// </summary>
    internal Func<int, TimeSpan> DictationDeadline { get; init; } = ScaledDictationDeadline;

    /// <summary>
    /// 3,000 characters. Measured on windows-vm (6 cores, emulated x64, Parakeet resident) with one segment per
    /// core: the translation alone took about 1.7 s at 3,600 characters and 2.3 to 2.7 s at 5,400. End-to-end runs
    /// with a 5,000-character selection, including the preview, needed 3.02 to 3.55 s in 4 of 16 warm runs, so the
    /// limit was lowered to 4,000 to keep the whole path under the three-second requirement. The latency gate
    /// (a selection of exactly 4,000 unpunctuated English characters into German on a warm model) then took 3.06
    /// to 3.62 s, so the limit is 3,000.
    /// </summary>
    internal const int DefaultSelectionMaxCharacters = 3000;

    internal static TranslationLimits Default { get; } = new();

    private static TimeSpan ScaledDictationDeadline(int sourceTokens) =>
        TimeSpan.FromMilliseconds(Math.Min(90_000L, 3_000L + 60L * Math.Max(0, sourceTokens)));
}
