using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Automation.Cron;
using OneCode.Core.Cron;

namespace OneCode.Tests;

public sealed class CronAtomicityTests : IDisposable
{
    private readonly CronSchedulerService _scheduler;

    public CronAtomicityTests()
    {
        _scheduler = new CronSchedulerService(
            NullLogger<CronSchedulerService>.Instance,
            Substitute.For<ICronParser>(),
            Substitute.For<ICronJobExecutor>());
    }

    [Fact]
    public async Task TryAddJobAsync_ConcurrentCreates_NeverExceedsLimit()
    {
        var attempts = Enumerable.Range(0, CronSchedulerService.MaxJobs * 4)
            .Select(i => _scheduler.TryAddJobAsync(NewJob(i)))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        results.Count(static success => success).Should().Be(CronSchedulerService.MaxJobs);
        _scheduler.GetJobs().Should().HaveCount(CronSchedulerService.MaxJobs);
        _scheduler.GetJobs().Select(static job => job.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TryAddJobAsync_DuplicateId_IsRejectedAtomically()
    {
        var job = NewJob(1);
        var results = await Task.WhenAll(
            _scheduler.TryAddJobAsync(job),
            _scheduler.TryAddJobAsync(job));

        results.Should().ContainSingle(static value => value);
        _scheduler.GetJobs().Should().ContainSingle();
    }

    private static CronJobEntry NewJob(int index) => new()
    {
        Id = $"job{index:D5}",
        Cron = "* * * * *",
        Prompt = $"prompt {index}",
        Recurring = true,
        NextRunAt = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(),
    };

    public void Dispose() => _scheduler.Dispose();
}
