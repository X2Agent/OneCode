using OneCode.Core.Cron;
using Cronos;

namespace OneCode.Automation.Cron;

public sealed class CronosCronParser : ICronParser
{
    public DateTimeOffset? ComputeNextRun(string cronExpression, DateTimeOffset after)
    {
        try
        {
            var cron = CronExpression.Parse(cronExpression);
            return cron.GetNextOccurrence(after, TimeZoneInfo.Local);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    public bool IsValid(string cronExpression)
    {
        try
        {
            CronExpression.Parse(cronExpression);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
