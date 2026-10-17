using System.Globalization;
using System.Text.RegularExpressions;

namespace WinWispr.Core;

internal static class TaskBoardQuery
{
    internal static IReadOnlyList<BoardTask> VisibleTasks(
        TaskBoardDocument document,
        BoardTaskStatus status,
        string? search,
        Guid? tagFilter,
        bool includeArchived)
    {
        IEnumerable<BoardTask> query = document.Tasks.Where(task =>
            !TaskBoardOperations.IsDeleted(task)
            && task.Status == status
            && (includeArchived || !task.ArchivedUtc.HasValue)
            && Matches(task, document.Tags, search, tagFilter));

        return Sort(query, document.Tags, document.View.Sort).ToArray();
    }

    internal static IReadOnlyList<BoardTask> ArchivedTasks(
        TaskBoardDocument document,
        string? search,
        Guid? tagFilter) => document.Tasks
        .Where(task => !TaskBoardOperations.IsDeleted(task)
            && task.ArchivedUtc.HasValue
            && task.Status == BoardTaskStatus.Done
            && Matches(task, document.Tags, search, tagFilter))
        .OrderByDescending(task => task.ArchivedUtc)
        .ThenBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    internal static IReadOnlyList<BoardTask> DueOrOverdueOpenTasks(TaskBoardDocument document, DateOnly today) =>
        document.Tasks
            .Where(task => !TaskBoardOperations.IsDeleted(task)
                && !task.ArchivedUtc.HasValue
                && task.Status is BoardTaskStatus.ToDo or BoardTaskStatus.Doing
                && task.DueDate is DateOnly due && due <= today)
            .OrderBy(task => task.DueDate)
            .ThenBy(task => task.Status)
            .ThenBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    internal static bool Matches(
        BoardTask task,
        IReadOnlyList<BoardTag> tags,
        string? search,
        Guid? tagFilter)
    {
        if (tagFilter is Guid id && !task.TagIds.Contains(id))
            return false;
        if (string.IsNullOrWhiteSpace(search))
            return true;

        string haystack = string.Join('\n', new[] { task.Title, task.Description }
            .Concat(task.TagIds.Select(tagId => tags.FirstOrDefault(tag => tag.Id == tagId)?.Name ?? string.Empty)));
        return haystack.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    internal static IEnumerable<BoardTask> Sort(
        IEnumerable<BoardTask> tasks,
        IReadOnlyList<BoardTag> tags,
        BoardTaskSort sort)
    {
        return sort switch
        {
            BoardTaskSort.TitleAscending => tasks.OrderBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(task => task.ManualOrder),
            BoardTaskSort.TitleDescending => tasks.OrderByDescending(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(task => task.ManualOrder),
            BoardTaskSort.DueAscending => tasks.OrderBy(task => task.DueDate.HasValue ? 0 : 1)
                .ThenBy(task => task.DueDate)
                .ThenBy(task => task.ManualOrder),
            BoardTaskSort.DueDescending => tasks.OrderBy(task => task.DueDate.HasValue ? 0 : 1)
                .ThenByDescending(task => task.DueDate)
                .ThenBy(task => task.ManualOrder),
            BoardTaskSort.CreatedDescending => tasks.OrderByDescending(task => task.CreatedUtc)
                .ThenBy(task => task.ManualOrder),
            BoardTaskSort.CreatedAscending => tasks.OrderBy(task => task.CreatedUtc)
                .ThenBy(task => task.ManualOrder),
            _ => tasks.OrderBy(task => task.ManualOrder)
                .ThenBy(task => task.CreatedUtc)
        };
    }
}

internal sealed record TaskDraftParseResult(
    string OriginalTranscript,
    string Description,
    string SuggestedTitle,
    DateOnly? SuggestedDueDate,
    string? UnresolvedDatePhrase,
    IReadOnlyList<Guid> SelectedTagIds,
    IReadOnlyList<Guid> SuggestedTagIds,
    IReadOnlyList<string> ExplicitTagNames);

internal static class TaskDraftParser
{
    private static readonly Regex ExplicitTags = new(
        @"(?<!\p{L})(?:tags?|labels?)\s+(?<tags>[^.!?\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex DatePhrase = new(
        @"(?<phrase>\bheute\b|\bmorgen\b|\bübermorgen\b|\btoday\b|\btomorrow\b|\bthe\s+day\s+after\s+tomorrow\b|\b(?:in\s+\d+\s+(?:days?|tagen?))\b|\b(?:am\s+)?\d{1,2}[./]\d{1,2}(?:[./]\d{2,4})?\b|\b(?:montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b|\bnächste\s+woche\b|\bnext\s+week\b|\birgendwann\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    internal static TaskDraftParseResult Parse(
        string transcript,
        IReadOnlyList<BoardTag> existingTags,
        DateOnly today)
    {
        transcript ??= string.Empty;
        string text = transcript.Trim();
        Match? tagMatch = ExplicitTags.Match(text) is { Success: true } match ? match : null;
        var explicitNames = new List<string>();
        var selected = new List<Guid>();
        if (tagMatch is not null)
        {
            foreach (string name in SplitTagNames(tagMatch.Groups["tags"].Value))
            {
                BoardTag? existing = existingTags.FirstOrDefault(tag =>
                    string.Equals(Normalize(name), Normalize(tag.Name), StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    selected.Add(existing.Id);
                else if (!explicitNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    explicitNames.Add(name);
            }
        }

        var suggested = existingTags
            .Where(tag => ContainsWholeWord(text, tag.Name) && !selected.Contains(tag.Id))
            .Select(tag => tag.Id)
            .ToArray();

        DateOnly? due = null;
        string? unresolved = null;
        MatchCollection dateMatches = DatePhrase.Matches(text);
        Match? dateMatch = dateMatches.Count > 0 ? dateMatches[0] : null;
        if (dateMatch is not null)
        {
            if (dateMatches.Count > 1)
            {
                unresolved = string.Join(", ", dateMatches.Select(match => match.Groups["phrase"].Value));
            }
            else
            {
            string phrase = dateMatch.Groups["phrase"].Value;
            string normalized = Normalize(phrase);
            if (normalized is "heute" or "today")
                due = today;
            else if (normalized is "morgen" or "tomorrow")
                due = today.AddDays(1);
            else if (normalized is "übermorgen" or "the day after tomorrow")
                due = today.AddDays(2);
            else if (Regex.Match(normalized, @"^(?:in)\s+(\d+)\s+(?:days?|tagen?)$", RegexOptions.CultureInvariant) is { Success: true } relative
                     && int.TryParse(relative.Groups[1].Value, out int days) && days <= 366)
                due = today.AddDays(days);
            else if (Regex.IsMatch(phrase, @"^\s*\d{1,2}/\d{1,2}\s*$"))
                unresolved = phrase;
            else if (TryParseDate(phrase, today, out DateOnly parsed))
                due = parsed;
            else
                unresolved = phrase;
            }
        }

        string title = BuildTitle(text, tagMatch, dateMatches);
        return new TaskDraftParseResult(text, text, title, due, unresolved, selected, suggested, explicitNames);
    }

    internal static string Normalize(string value) => string.Join(' ', value.Trim()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ',', ';');

    private static IEnumerable<string> SplitTagNames(string input)
    {
        string cleaned = Normalize(input);
        cleaned = Regex.Replace(cleaned, @"\s+(?:und|and|&)\s+", ",", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0);
    }

    private static string BuildTitle(string text, Match? tagMatch, IEnumerable<Match> dateMatches)
    {
        string title = text;
        foreach (Match match in new[] { tagMatch }
                     .Where(match => match is not null)
                     .Cast<Match>()
                     .Concat(dateMatches)
                     .OrderByDescending(match => match.Index))
        {
            int count = Math.Min(match.Length, Math.Max(0, title.Length - match.Index));
            if (count > 0 && match.Index < title.Length)
                title = title.Remove(match.Index, count);
        }
        title = Regex.Replace(title, @"\s+", " ", RegexOptions.CultureInvariant).Trim(' ', ',', ';', ':');
        Match sentence = Regex.Match(title, @"^(.+?)(?:[.!?](?=\s|$)|$)", RegexOptions.CultureInvariant);
        if (sentence.Success && sentence.Groups[1].Value.Length > 0)
            title = sentence.Groups[1].Value.Trim(' ', ',', ';', ':');
        if (title.Length == 0)
            title = "New task";
        if (title.Length <= 96)
            return title;
        int cut = title.LastIndexOf(' ', 95);
        return (cut > 20 ? title[..cut] : title[..96]).TrimEnd() + "…";
    }

    private static bool ContainsWholeWord(string text, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string pattern = $"(?<![\\p{{L}}\\p{{N}}_]){Regex.Escape(value)}(?![\\p{{L}}\\p{{N}}_])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static bool TryParseDate(string phrase, DateOnly today, out DateOnly date)
    {
        date = default;
        string cleaned = Regex.Replace(phrase.Trim(), @"^(?:am\s+)", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Match numeric = Regex.Match(cleaned, @"^(?<day>\d{1,2})(?<separator>[./])(?<month>\d{1,2})(?:(?:[./])(?<year>\d{2,4}))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!numeric.Success
            || !int.TryParse(numeric.Groups["day"].Value, CultureInfo.InvariantCulture, out int day)
            || !int.TryParse(numeric.Groups["month"].Value, CultureInfo.InvariantCulture, out int month))
            return false;

        if (numeric.Groups["year"].Success)
        {
            if (!int.TryParse(numeric.Groups["year"].Value, CultureInfo.InvariantCulture, out int explicitYear))
                return false;
            if (explicitYear < 100)
                explicitYear += 2000;
            return TryCreateDate(explicitYear, month, day, out date);
        }

        int year = today.Year;
        if (month < today.Month || month == today.Month && day < today.Day)
            year++;
        return TryCreateDate(year, month, day, out date);
    }

    private static bool TryCreateDate(int year, int month, int day, out DateOnly date)
    {
        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            date = default;
            return false;
        }
    }
}
