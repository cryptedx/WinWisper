using System.Text;
using System.Text.RegularExpressions;

namespace WinWispr.Core;

internal static class DictationTextProcessor
{
    private const string WordCharacter = @"[\p{L}\p{M}\p{N}_]";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    internal static string Process(string text, AppSettings settings, PersonalDictionary dictionary)
    {
        if (settings.VoiceCommandsEnabled)
        {
            var formatted = new StringBuilder();
            int position = 0;
            foreach (Match match in Regex.Matches(text,
                @"[ \t]*(?<!" + WordCharacter + @")(?<command>neuer absatz|neue zeile|aufzählung|doppelpunkt)(?!"
                    + WordCharacter + @")[.,!?]?[ \t]*", Options, TimeSpan.FromSeconds(1)))
            {
                formatted.Append(text, position, match.Index - position);
                formatted.Append(match.Groups["command"].Value.ToLowerInvariant() switch
                {
                    "neuer absatz" => "\r\n\r\n",
                    "neue zeile" => "\r\n",
                    "aufzählung" => formatted.Length == 0 || formatted[^1] == '\n' ? "• " : "\r\n• ",
                    _ => ": "
                });
                position = match.Index + match.Length;
            }
            text = formatted.Append(text, position, text.Length - position).ToString();
        }

        IReadOnlyDictionary<string, string> entries = dictionary.Entries;
        if (entries.Count > 0)
        {
            // One pass prevents replacements from being processed again as dictionary input.
            string pattern = "(?<!" + WordCharacter + ")(?:"
                + string.Join("|", entries.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape))
                + ")(?!" + WordCharacter + ")";
            text = Regex.Replace(text, pattern, match => entries[match.Value], Options, TimeSpan.FromSeconds(1));
        }
        return text.Trim();
    }
}
