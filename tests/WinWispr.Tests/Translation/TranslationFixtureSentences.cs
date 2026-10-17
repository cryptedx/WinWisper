using System.Text.RegularExpressions;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// Single sentences taken from the qualification fixtures (ticket 02, acceptance pack), with the facts that the
/// translation must keep: numbers, dates, times, product and place names, and negation. The expected fragments come
/// from the qualification's stored greedy outputs; they are fragments, not whole sentences, so the tests do not
/// depend on wording that the model may vary. Every sentence is one sentence of the fixture case named in
/// <see cref="Case"/>; whole texts and sentence splitting belong to the whole-text translation.
/// </summary>
internal sealed record FixtureSentence(
    TranslationDirection Direction,
    string Case,
    string Source,
    string[] MustContain,
    string[] MustMatch)
{
    private static FixtureSentence DeEn(string id, string source, string[] contain, params string[] match) =>
        new(TranslationDirection.GermanToEnglish, id, source, contain, match);

    private static FixtureSentence EnDe(string id, string source, string[] contain, params string[] match) =>
        new(TranslationDirection.EnglishToGerman, id, source, contain, match);

    internal static readonly IReadOnlyList<FixtureSentence> All =
    [
        // German to English.
        DeEn("de-en-dev-05", "Die Rechnung über 1.249,50 Euro ist am 15. März 2026 fällig, die Lieferung wiegt 12,5 Kilogramm.",
            ["1,249.50", "12.5", "2026"]),
        DeEn("de-en-dev-06", "Das Treffen findet von 9:30 bis 11:00 Uhr in Raum 4.12 statt, und wir erwarten etwa 25 Teilnehmer.",
            ["9:30", "11:00", "4.12", "25"]),
        // The product name Teams changes to Team in the qualification's output; the other names must stay.
        DeEn("de-en-dev-02", "Ich habe die Präsentation in Microsoft PowerPoint erstellt und sie anschließend im Teams-Kanal von Projekt Nordlicht geteilt.",
            ["PowerPoint", "Nordlicht"]),
        DeEn("de-en-held-03", "Bitte öffne die Datei in Visual Studio Code und installiere die Erweiterung für PowerShell.",
            ["Visual Studio Code", "PowerShell"]),
        DeEn("de-en-held-04", "Das Dashboard in Power BI zeigt die Zahlen aus SAP, und der Export nach Excel funktioniert auch.",
            ["Power BI", "SAP", "Excel"]),
        DeEn("de-en-held-07", "Am 3. Oktober 2026 fahren wir 450 Kilometer nach München und kommen gegen 18:45 Uhr an.",
            ["450", "Munich", "2026", "18:45"]),
        DeEn("de-en-held-08", "Die Miete beträgt 890 Euro im Monat, die Kaution 2.670 Euro, und der Vertrag läuft bis zum 31.12.2027.",
            ["890", "2,670", "2027"]),
        DeEn("de-en-held-10", "Wir können den Termin am Dienstag leider nicht wahrnehmen, aber am Mittwoch haben wir keine Einwände.",
            ["Tuesday", "Wednesday", "no objections"], @"\b(cannot|can't|can not)\b"),
        DeEn("de-en-dev-07", "Das Update wurde nicht installiert, weil die Prüfsumme nicht stimmte.",
            ["installed"], @"\bnot\b|n't"),
        DeEn("de-en-held-09", "Ich habe die Datei nicht gelöscht, sondern nur verschoben.",
            ["moved"], @"\bnot\b|n't"),

        // English to German.
        EnDe("en-de-dev-05", "The invoice for 1,249.50 euros is due on March 15, 2026, and the shipment weighs 12.5 kilograms.",
            ["1.249,50", "12,5", "2026"]),
        EnDe("en-de-dev-06", "The meeting runs from 9:30 to 11:00 a.m. in room 4.12, and we expect about 25 participants.",
            ["9:30", "11:00", "4.12", "25"]),
        EnDe("en-de-dev-02", "I created the spreadsheet in Microsoft Excel and shared it in the Teams channel of Project Atlas.",
            ["Excel", "Atlas"]),
        EnDe("en-de-held-03", "Please open the file in Visual Studio Code and install the extension for Python.",
            ["Visual Studio Code", "Python"]),
        EnDe("en-de-held-04", "Our tickets are in Jira, but the agreements happen in Slack and in GitHub comments, not by email.",
            ["Jira", "Slack", "GitHub"]),
        EnDe("en-de-held-07", "On October 3, 2026, we drive 450 kilometers to Munich and arrive at around 6:45 p.m.",
            ["450", "München", "18:45", "2026"]),
        EnDe("en-de-held-08", "The rent is 1,150 euros a month, the deposit is 3,450 euros, and the contract runs until June 30, 2028.",
            ["1.150", "3.450", "2028"]),
        EnDe("en-de-held-09", "We have not changed the password, and nobody outside the team has access to the account.",
            ["niemand"], @"\bnicht\b"),
        EnDe("en-de-held-10", "I can't join the workshop on Monday, but I'm not against moving it to Thursday.",
            ["Montag", "Donnerstag"], @"\bnicht\b"),
        EnDe("en-de-dev-07", "The patch was not applied because the signature was invalid.",
            ["Patch"], @"\bnicht\b")
    ];

    /// <summary>The facts the translation lost, or an empty list.</summary>
    internal IReadOnlyList<string> Violations(string output)
    {
        var problems = new List<string>();
        foreach (string fragment in MustContain)
            if (!output.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                problems.Add($"missing \"{fragment}\"");
        foreach (string pattern in MustMatch)
            if (!Regex.IsMatch(output, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                problems.Add($"no match for /{pattern}/");
        return problems;
    }
}
