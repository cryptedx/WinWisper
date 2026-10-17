using System.Text.RegularExpressions;

namespace WinWispr.Translation;

/// <summary>
/// A German or English detector without a library: function words, words with umlauts or the sharp s, and English
/// contractions (QUALIFICATION.md section 5, measured on the qualification fixtures, never confidently wrong).
/// Every cue is worth one point except a word with the sharp s (two points). Points from umlauts and the sharp s
/// together are capped, so a name such as "Mötley Crüe" cannot decide alone. Text inside quotation marks is read
/// last: a quoted foreign clause must not decide the language of the sentence around it.
/// </summary>
/// <remarks>
/// Rule: no letters give <see cref="DetectedLanguage.NoLetters"/>; evidence for one language only gives that
/// language; otherwise a lead of at least two points and at least 60 percent of all points gives the leader; else
/// <see cref="DetectedLanguage.Ambiguous"/>. It never throws.
/// </remarks>
internal static class LanguageDetector
{
    private const int DiacriticCap = 4;
    private const double LeadShare = 0.60;

    private static readonly Regex Quoted = new(
        "\"[^\"]*\"|„[^“”\"]*[“”\"]|“[^”]*”|«[^»]*»|‹[^›]*›",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A word: letters, optionally with one apostrophe part such as can't, I'm, it's.
    private static readonly Regex Word = new(
        @"\p{L}+(?:['’]\p{L}+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Contraction = new(
        @"^\p{L}*n['’]t$|^\p{L}+['’](?:m|re|ve|ll|d)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Words that exist in both languages with different meanings are left out on purpose (was, war, die, an, in, am,
    // so, bin, hat, kind, gift, also, will, man, her, fast).
    private static readonly HashSet<string> German = new(StringComparer.Ordinal)
    {
        "der", "den", "dem", "des", "das", "ein", "eine", "einen", "einem", "einer", "eines", "und", "oder", "aber",
        "nicht", "kein", "keine", "keinen", "ist", "sind", "wird", "werden", "wurde", "wurden", "haben", "hatte", "ich",
        "du", "er", "es", "sie", "wir", "ihr", "mit", "von", "zu", "zum", "zur", "bei", "nach", "aus", "auf", "für",
        "über", "unter", "vor", "auch", "noch", "schon", "nur", "wenn", "dass", "weil", "wie", "wer", "wann", "bitte",
        "danke", "ja", "nein", "mich", "mir", "dich", "dir", "sich", "mein", "meine", "dein", "deine", "sein", "seine",
        "unser", "unsere", "im", "dann", "als", "hier", "jetzt", "heute", "gestern", "morgen", "sehr", "ob", "doch",
        "mal", "kann", "kannst", "können", "muss", "müssen", "soll", "sollen", "wollen", "gut", "guten", "vielen",
        "hallo", "tschüss", "bis", "nichts", "alle", "alles", "diese", "dieser", "dieses", "durch", "gegen", "ohne",
        "zwischen", "während"
    };

    private static readonly HashSet<string> English = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "not", "no", "is", "are", "was", "were", "be", "been", "has", "have",
        "had", "do", "does", "did", "i", "you", "he", "she", "it", "we", "they", "me", "him", "us", "them", "my",
        "your", "his", "its", "our", "their", "with", "from", "to", "of", "on", "at", "by", "for", "about", "into",
        "over", "before", "after", "also", "still", "already", "only", "if", "that", "because", "how", "what", "who",
        "where", "when", "please", "thanks", "thank", "yes", "this", "these", "those", "there", "here", "can", "could",
        "would", "should", "must", "may", "might", "very", "today", "tomorrow", "yesterday", "just", "then", "than",
        "as", "up", "out", "good", "morning", "hello", "hi", "bye", "sorry", "nothing", "all", "every", "through",
        "against", "without", "between", "during", "although", "some"
    };

    internal static DetectedLanguage Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return DetectedLanguage.NoLetters;

        string outside = Quoted.Replace(text, " ");
        if (!ReferenceEquals(outside, text))
        {
            (DetectedLanguage verdict, int total) = Score(outside);
            if (total > 0)
                return verdict;
        }

        return Score(text).Verdict;
    }

    private static (DetectedLanguage Verdict, int Total) Score(string text)
    {
        int german = 0, english = 0, words = 0, diacritic = 0;
        foreach (Match match in Word.Matches(text))
        {
            words++;
            string word = match.Value.ToLowerInvariant();
            if (Contraction.IsMatch(word))
            {
                english++;
                continue;
            }

            int apostrophe = word.IndexOfAny(['\'', '’']);
            string baseWord = apostrophe > 0 ? word[..apostrophe] : word;
            if (German.Contains(baseWord))
                german++;
            if (English.Contains(baseWord))
                english++;
            if (baseWord.Contains('ß'))
                diacritic += 2;
            else if (baseWord.AsSpan().IndexOfAny('ä', 'ö', 'ü') >= 0)
                diacritic += 1;
        }

        if (words == 0)
            return (DetectedLanguage.NoLetters, 0);

        german += Math.Min(diacritic, DiacriticCap);
        int total = german + english;
        if (total == 0)
            return (DetectedLanguage.Ambiguous, 0);
        if (english == 0)
            return (DetectedLanguage.German, total);
        if (german == 0)
            return (DetectedLanguage.English, total);

        int lead = Math.Abs(german - english);
        double share = Math.Max(german, english) / (double)total;
        if (lead >= 2 && share >= LeadShare)
            return (german > english ? DetectedLanguage.German : DetectedLanguage.English, total);
        return (DetectedLanguage.Ambiguous, total);
    }
}
