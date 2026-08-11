namespace OneCode.Core.Cron;

/// <summary>
/// Pure string-based cron expression formatting utilities.
/// No external library dependency — these operate on raw cron string syntax.
/// </summary>
public static class CronExpressionHelper
{
    /// <summary>
    /// Convert a 5-field cron expression to a human-readable description.
    /// </summary>
    public static string CronToHumanReadable(string cronExpression)
    {
        var parts = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return cronExpression;

        if (parts is ["*", "*", "*", "*", "*"])
            return "Every minute";

        if (parts is ["*/15", "*", "*", "*", "*"])
            return "Every 15 minutes";

        if (parts is ["0", "*/1", "*", "*", "*"] or ["0", "*", "*", "*", "*"])
            return "Every hour";

        if (parts is ["0", "0", "*", "*", "*"])
            return "Every day at midnight";

        if (parts[0] != "*" && parts[1] != "*" && parts[2] == "*" && parts[3] == "*" && parts[4] == "*")
            return $"Daily at {parts[1]}:{parts[0].PadLeft(2, '0')}";

        List<string> descriptions = [];
        if (parts[0] != "*") descriptions.Add($"minute {parts[0]}");
        if (parts[1] != "*") descriptions.Add($"hour {parts[1]}");
        if (parts[2] != "*") descriptions.Add($"day-of-month {parts[2]}");
        if (parts[3] != "*") descriptions.Add($"month {parts[3]}");
        if (parts[4] != "*") descriptions.Add($"day-of-week {parts[4]}");

        return descriptions.Count == 0 ? "Every minute" : string.Join(", ", descriptions);
    }
}
