namespace WinWispr.Core;

internal sealed record TaskDailySummary(int Count, IReadOnlyList<string> Titles)
{
    internal bool HasTasks => Count > 0;

    internal static TaskDailySummary Create(TaskBoardDocument document, DateOnly today)
    {
        IReadOnlyList<BoardTask> due = TaskBoardQuery.DueOrOverdueOpenTasks(document, today);
        return new TaskDailySummary(due.Count, due.Take(3).Select(task => task.Title).ToArray());
    }

    internal string NotificationTitle => HasTasks ? $"{Count} task{(Count == 1 ? string.Empty : "s")} due" : "No tasks due";

    internal string NotificationBody
    {
        get
        {
            if (!HasTasks)
                return "You have no overdue or due open tasks.";
            string suffix = Count > Titles.Count ? $"{Environment.NewLine}+{Count - Titles.Count} more" : string.Empty;
            string body = string.Join(Environment.NewLine, Titles) + suffix;
            if (body.Length <= 220)
                return body;
            int titleBudget = Math.Max(0, 220 - suffix.Length - 1);
            string titles = string.Join(Environment.NewLine, Titles);
            return titles[..Math.Min(titleBudget, titles.Length)].TrimEnd() + "…" + suffix;
        }
    }
}
