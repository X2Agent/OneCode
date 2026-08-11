using System.ComponentModel;
using System.Text.Json;
using OneCode.Core.Cron;

namespace OneCode.Automation.Cron;

/// <summary>Cron tools — create, list, delete, pause, and resume scheduled cron jobs.</summary>
/// <remarks>
/// 所有 Cron 工具依赖 <see cref="ICronParser"/> 与 <see cref="CronSchedulerService"/>，
/// 两者通过 <c>AddCronTools</c> 的 DI 工厂以 <c>GetRequiredService</c> 解析，
/// 要求 host 先调用 <c>AddCronScheduler</c> 注册调度器。
/// </remarks>

public sealed class CronCreateTool
{
    private readonly ICronParser _cronParser;
    private readonly CronSchedulerService _scheduler;

    public CronCreateTool(ICronParser cronParser, CronSchedulerService scheduler)
    {
        _cronParser = cronParser;
        _scheduler = scheduler;
    }

    [Description("Create a scheduled cron task. Supports standard 5-field cron expressions.")]
    public async Task<string> CreateAsync(
        [Description("Standard 5-field cron expression (local time).")] string cron,
        [Description("The prompt to enqueue on each trigger.")] string prompt,
        [Description("true=recurring, false=one-shot (default true).")] bool recurring = true,
        [Description("true=persist to disk so the job survives process restarts. Requires ONECODE_DURABLE_CRON=true; otherwise forced to false.")] bool durable = false,
        CancellationToken ct = default)
    {
        if (!ValidateCron(cron)) return $"{{\"error\":\"Invalid cron expression: {cron}\"}}";
        if (string.IsNullOrWhiteSpace(prompt)) return "{\"error\":\"prompt is required\"}";

        var nextRun = _cronParser.ComputeNextRun(cron, DateTimeOffset.UtcNow);
        if (nextRun == null) return $"{{\"error\":\"No future matches for: {cron}\"}}";

        var id = Guid.NewGuid().ToString("N")[..8];
        var effectiveDurable = durable && CronPaths.IsDurableCronEnabled();

        var entry = new CronJobEntry
        {
            Id = id,
            Cron = cron,
            Prompt = prompt,
            Recurring = recurring,
            Durable = effectiveDurable,
            Paused = false,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            NextRunAt = nextRun.Value.ToUnixTimeSeconds(),
        };

        if (!await _scheduler.TryAddJobAsync(entry, ct).ConfigureAwait(false))
            return $"{{\"error\":\"Max {CronSchedulerService.MaxJobs} cron jobs reached or job could not be persisted\"}}";

        return JsonSerializer.Serialize(new
        {
            id,
            recurring,
            durable = effectiveDurable,
            humanSchedule = CronExpressionHelper.CronToHumanReadable(cron),
            nextRunAt = entry.NextRunAt,
        });
    }

    private bool ValidateCron(string cron) => _cronParser.IsValid(cron);
}

public sealed class CronDeleteTool
{
    private readonly CronSchedulerService _scheduler;

    public CronDeleteTool(CronSchedulerService scheduler) => _scheduler = scheduler;

    [Description("Delete a scheduled cron job by ID.")]
    public string Delete([Description("Cron job ID.")] string id)
    {
        return _scheduler.TryRemoveJob(id)
            ? JsonSerializer.Serialize(new { id, status = "deleted" })
            : $"{{\"error\":\"Cron job '{id}' not found\"}}";
    }
}

public sealed class CronListTool
{
    private readonly CronSchedulerService _scheduler;

    public CronListTool(CronSchedulerService scheduler)
    {
        _scheduler = scheduler;
    }

    [Description("List all scheduled cron jobs.")]
    public Task<string> ListAsync(CancellationToken ct = default)
    {
        var jobs = _scheduler.GetJobs();
        var result = jobs.Select(j => new
        {
            j.Id,
            j.Cron,
            humanSchedule = CronExpressionHelper.CronToHumanReadable(j.Cron),
            promptPreview = j.Prompt.Length > 80 ? j.Prompt[..80] + "..." : j.Prompt,
            j.Recurring,
            j.Durable,
            j.Paused,
            j.LastRunAt,
            j.NextRunAt,
        });

        return Task.FromResult(JsonSerializer.Serialize(result));
    }
}

public sealed class CronPauseTool
{
    private readonly CronSchedulerService _scheduler;

    public CronPauseTool(CronSchedulerService scheduler) => _scheduler = scheduler;

    [Description("Pause a scheduled cron job. The job's schedule history is preserved; resume with CronResume.")]
    public async Task<string> PauseAsync([Description("Cron job ID.")] string id, CancellationToken ct = default)
    {
        var ok = await _scheduler.TrySetPausedAsync(id, paused: true, ct).ConfigureAwait(false);
        return ok
            ? JsonSerializer.Serialize(new { id, status = "paused" })
            : $"{{\"error\":\"Cron job '{id}' not found\"}}";
    }
}

public sealed class CronResumeTool
{
    private readonly CronSchedulerService _scheduler;

    public CronResumeTool(CronSchedulerService scheduler) => _scheduler = scheduler;

    [Description("Resume a paused cron job. Recurring jobs recompute their next run from now, so missed occurrences are not backfilled.")]
    public async Task<string> ResumeAsync([Description("Cron job ID.")] string id, CancellationToken ct = default)
    {
        var ok = await _scheduler.TrySetPausedAsync(id, paused: false, ct).ConfigureAwait(false);
        return ok
            ? JsonSerializer.Serialize(new { id, status = "resumed" })
            : $"{{\"error\":\"Cron job '{id}' not found\"}}";
    }
}
