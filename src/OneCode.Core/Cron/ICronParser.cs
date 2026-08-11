namespace OneCode.Core.Cron;

public interface ICronParser
{
    DateTimeOffset? ComputeNextRun(string cronExpression, DateTimeOffset after);
    bool IsValid(string cronExpression);
}
