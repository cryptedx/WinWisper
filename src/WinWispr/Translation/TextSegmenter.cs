using System.Text;
using System.Text.RegularExpressions;

namespace WinWispr.Translation;

/// <summary>
/// Cuts a text into the segments the model translates and the literal text between them: line breaks, blank lines,
/// indentation, list markers, the spaces between sentences, and lines without letters. Putting the translated
/// segments back where the segments were rebuilds the layout of the original. A segment is one sentence; a sentence
/// that has more tokens than the model handles is cut at word boundaries (and a single oversized word by
/// characters), so that nothing is ever truncated.
/// </summary>
internal static class TextSegmenter
{
    /// <summary>One piece of the text: literal text to keep, or a segment to translate with its token count.</summary>
    internal readonly record struct Part(string Text, bool IsSegment, int Tokens);

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Titles, forms of address and common abbreviations of both languages that end in a period without ending
        // the sentence. Words such as "etc." and "usw." are not listed: they often end a sentence, and a lowercase
        // continuation never splits anyway.
        "dr", "prof", "hr", "fr", "mr", "mrs", "ms", "jr", "sr", "st", "nr", "str", "tel", "ca", "bzw", "ggf", "evtl",
        "inkl", "zzgl", "vgl", "vs", "inc", "ltd", "approx", "dept", "fig", "abs", "bsp", "mio", "mrd", "geb",
        "jan", "feb", "mär", "apr", "jun", "jul", "aug", "sep", "sept", "okt", "nov", "dez", "oct", "dec"
    };

    private static readonly HashSet<string> GermanMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        "januar", "februar", "märz", "april", "mai", "juni", "juli", "august", "september", "oktober", "november",
        "dezember"
    };

    // Indentation and one list marker: a bullet, "1." or "1)", "a)" or "(1)", followed by blanks.
    private static readonly Regex ListMarker = new(
        @"^[ \t]*(?:[-*•–—▪◦·]|\d{1,3}[.)]|[a-z]\)|\(\d{1,3}\))[ \t]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NextWord = new(@"^\p{L}+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string Terminators = ".!?…";
    private const string Closers = "\"”’')]»›";
    private const string Openers = "„“\"'(«[¿¡‹‚";

    /// <summary>
    /// Splits <paramref name="text"/>. <paramref name="countTokens"/> counts the model tokens of a text and
    /// <paramref name="maxTokens"/> is the most one segment may have.
    /// </summary>
    internal static List<Part> Split(string text, Func<string, int> countTokens, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(countTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);
        var parts = new List<Part>();
        int position = 0;
        while (position < text.Length)
        {
            int lineBreak = text.AsSpan(position).IndexOfAny('\r', '\n');
            int lineEnd = lineBreak < 0 ? text.Length : position + lineBreak;
            SplitLine(text.Substring(position, lineEnd - position), parts, countTokens, maxTokens);
            if (lineBreak < 0)
                break;
            int breakLength = text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1;
            AddLiteral(parts, text.Substring(lineEnd, breakLength));
            position = lineEnd + breakLength;
        }

        return parts;
    }

    private static void SplitLine(string line, List<Part> parts, Func<string, int> countTokens, int maxTokens)
    {
        int start = LeadingLength(line);
        int end = line.Length;
        while (end > start && char.IsWhiteSpace(line[end - 1]))
            end--;
        AddLiteral(parts, line[..start]);
        if (end > start)
        {
            string body = line[start..end];
            int sentenceStart = 0;
            foreach ((int sentenceEnd, int next) in Boundaries(body))
            {
                AddSentence(parts, body[sentenceStart..sentenceEnd], countTokens, maxTokens);
                AddLiteral(parts, body[sentenceEnd..next]);
                sentenceStart = next;
            }

            AddSentence(parts, body[sentenceStart..], countTokens, maxTokens);
        }

        AddLiteral(parts, line[end..]);
    }

    /// <summary>The indentation and list marker at the start of a line, which are kept and not translated.</summary>
    private static int LeadingLength(string line)
    {
        Match marker = ListMarker.Match(line);
        if (!marker.Success)
        {
            int blanks = 0;
            while (blanks < line.Length && char.IsWhiteSpace(line[blanks]))
                blanks++;
            return blanks;
        }

        // "3. Oktober ist ein Feiertag." starts with a German date, not with a list marker.
        string text = marker.Value.TrimEnd(' ', '\t');
        if (text.EndsWith('.') && char.IsDigit(text[^2]))
        {
            Match word = NextWord.Match(line[marker.Length..]);
            if (word.Success && GermanMonths.Contains(word.Value))
            {
                int blanks = 0;
                while (blanks < line.Length && char.IsWhiteSpace(line[blanks]))
                    blanks++;
                return blanks;
            }
        }

        return marker.Length;
    }

    /// <summary>
    /// The sentence boundaries of one line: the end of a sentence (after its closing quotes) and the start of the
    /// next one. A boundary needs a terminator, blanks, and then a capital letter, a digit or an opening quote;
    /// abbreviations, initials, German ordinals and decimal numbers do not end a sentence.
    /// </summary>
    private static IEnumerable<(int End, int Next)> Boundaries(string body)
    {
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (Terminators.IndexOf(c) < 0)
                continue;

            int first = i;
            int last = i;
            while (last + 1 < body.Length && Terminators.IndexOf(body[last + 1]) >= 0)
                last++;
            int end = last;
            while (end + 1 < body.Length && Closers.IndexOf(body[end + 1]) >= 0)
                end++;
            i = end;
            if (end + 1 >= body.Length || !char.IsWhiteSpace(body[end + 1]))
                continue;

            int next = end + 1;
            while (next < body.Length && char.IsWhiteSpace(body[next]))
                next++;
            if (next >= body.Length || !StartsSentence(body[next]))
                continue;
            if (c == '.' && last == first && end == last && IsAbbreviation(body, first))
                continue;
            yield return (end + 1, next);
        }
    }

    private static bool StartsSentence(char c) => char.IsUpper(c) || char.IsDigit(c) || Openers.IndexOf(c) >= 0;

    /// <summary>True when the period at <paramref name="period"/> belongs to an abbreviation, initial or ordinal.</summary>
    private static bool IsAbbreviation(string body, int period)
    {
        if (period == 0 || body[period] != '.')
            return false;
        int tokenStart = period;
        while (tokenStart > 0 && (char.IsLetterOrDigit(body[tokenStart - 1]) || body[tokenStart - 1] == '.'))
            tokenStart--;
        string token = body[tokenStart..period];
        if (token.Length == 0)
            return false;
        if (token.Length <= 2 && token.All(char.IsDigit))
            return true; // a German ordinal such as "3."
        if (token.Length == 1 && char.IsLetter(token[0]))
            return true; // an initial, or one half of "z. B."
        if (token.Contains('.'))
        {
            // "a.m." and "p.m." usually end the sentence: a capital letter after them starts a new one.
            if (token.Equals("a.m", StringComparison.OrdinalIgnoreCase) || token.Equals("p.m", StringComparison.OrdinalIgnoreCase))
                return false;
            string[] pieces = token.Split('.');
            return pieces.All(piece => piece.Length is >= 1 and <= 2 && piece.All(char.IsLetter));
        }

        return Abbreviations.Contains(token);
    }

    private static void AddSentence(List<Part> parts, string sentence, Func<string, int> countTokens, int maxTokens)
    {
        if (sentence.Length == 0)
            return;
        if (!sentence.Any(char.IsLetter))
        {
            AddLiteral(parts, sentence);
            return;
        }

        int tokens = countTokens(sentence);
        if (tokens <= maxTokens)
        {
            parts.Add(new Part(sentence, true, tokens));
            return;
        }

        AddChunks(parts, sentence, countTokens, maxTokens);
    }

    /// <summary>Greedy word-boundary chunks; the blanks between chunks stay literal text.</summary>
    private static void AddChunks(List<Part> parts, string sentence, Func<string, int> countTokens, int maxTokens)
    {
        MatchCollection words = Regex.Matches(sentence, @"\S+");
        var chunk = new StringBuilder();
        int previousEnd = 0;
        foreach (Match word in words)
        {
            string blanks = sentence[previousEnd..word.Index];
            previousEnd = word.Index + word.Length;
            if (chunk.Length == 0)
            {
                AddLiteral(parts, blanks);
                chunk.Append(word.Value);
                continue;
            }

            string candidate = chunk + blanks + word.Value;
            if (countTokens(candidate) <= maxTokens)
            {
                chunk.Append(blanks).Append(word.Value);
                continue;
            }

            FlushChunk(parts, chunk.ToString(), countTokens, maxTokens);
            chunk.Clear();
            AddLiteral(parts, blanks);
            chunk.Append(word.Value);
        }

        if (chunk.Length > 0)
            FlushChunk(parts, chunk.ToString(), countTokens, maxTokens);
    }

    private static void FlushChunk(List<Part> parts, string chunk, Func<string, int> countTokens, int maxTokens)
    {
        int tokens = countTokens(chunk);
        if (tokens <= maxTokens)
        {
            if (chunk.Any(char.IsLetter))
                parts.Add(new Part(chunk, true, tokens));
            else
                AddLiteral(parts, chunk);
            return;
        }

        // One word alone is over the limit (a long identifier, a run of letters): cut it by characters.
        string rest = chunk;
        while (rest.Length > 0)
        {
            int length = LongestPrefix(rest, countTokens, maxTokens);
            string piece = rest[..length];
            if (piece.Any(char.IsLetter))
                parts.Add(new Part(piece, true, countTokens(piece)));
            else
                AddLiteral(parts, piece);
            rest = rest[length..];
        }
    }

    /// <summary>The longest prefix, at least one character, that fits the limit (binary search).</summary>
    private static int LongestPrefix(string text, Func<string, int> countTokens, int maxTokens)
    {
        int low = 1, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (countTokens(text[..middle]) <= maxTokens)
                low = middle;
            else
                high = middle - 1;
        }

        // Never cut a surrogate pair in half.
        if (low < text.Length && char.IsHighSurrogate(text[low - 1]) && low > 1)
            low--;
        return low;
    }

    private static void AddLiteral(List<Part> parts, string literal)
    {
        if (literal.Length == 0)
            return;
        if (parts.Count > 0 && !parts[^1].IsSegment)
            parts[^1] = new Part(parts[^1].Text + literal, false, 0);
        else
            parts.Add(new Part(literal, false, 0));
    }
}
