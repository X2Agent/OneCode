using System.ComponentModel;
using OneCode.Core.Cron;
using OneCode.Core.Tools;

namespace OneCode.Automation.Cron;

/// <summary>
/// Unified Cron tool — manages scheduled cron jobs via action-based routing.
/// Replaces the former CronCreate/CronList/CronDelete/CronPause/CronResume tools.
/// </summary>
/// <remarks>
/// 依赖 <see cref="ICronParser"/> 与 <see cref="CronSchedulerService"/>，两者通过
/// <c>AddCronTools</c> 的 DI 工厂以 <c>GetRequiredService</c> 解析，要求 host 先调用
/// <c>AddCronScheduler</c> 注册调度器。
/// </remarks>
public sealed class CronTool(ICronParser cronParser, CronSchedulerService scheduler)
{
    [Description("Manage scheduled cron jobs: create, list, delete, pause, or resume. " +
                 "Use 'create' with a 5-field cron expression and prompt; 'list' to see all jobs; " +
                 "'delete'/'pause'/'resume' take a job ID.")]
    public Task<ToolResult> ExecuteAsync(
        [Description("Action: create, list, delete, pause, resume.")] string action,
        [Description("Standard 5-field cron expression (local time, for create).")] string? cron = null,
        [Description("The prompt to enqueue on each trigger (for create).")] string? prompt = null,
        [Description("Cron job ID (for delete/pause/resume).")] string? id = null,
        [Description("true=recurring, false=one-shot (for create, default true).")] bool recurring = true,
        [Description("true=persist to disk so the job survives process restarts (for create). Requires ONECODE_DURABLE_CRON=true; otherwise forced to false.")] bool durable = false,
        CancellationToken ct = default)
    {
        return action.ToLowerInvariant() switch
        {
            "create" => CreateAsync(cron, prompt, recurring, durable, ct),
            "list" => Task.FromResult(List()),
            "delete" => Task.FromResult(Delete(id)),
            "pause" => PauseAsync(id, ct),
            "resume" => ResumeAsync(id, ct),
            _ => Task.FromResult(ToolResult.Error(
                $"Unknown action '{action}'. Valid: create, list, delete, pause, resume.")),
        };
    }

    private async Task<ToolResult> CreateAsync(
        string? cron, string? prompt, bool recurring, bool durable, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cron) || !cronParser.IsValid(cron))
            return ToolResult.Error($"Invalid cron expression: {cron}");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("prompt is required");

        var nextRun = cronParser.ComputeNextRun(cron, DateTimeOffset.UtcNow);
        if (nextRun is null)
            return ToolResult.Error($"No future matches for: {cron}");

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

        if (!await scheduler.TryAddJobAsync(entry, ct).ConfigureAwait(false))
            return ToolResult.Error(
                $"Max {CronSchedulerService.MaxJobs} cron jobs reached or job could not be persisted");

        return ToolResult.JsonSuccess(new
        {
            id,
            recurring,
            durable = effectiveDurable,
            humanSchedule = CronExpressionHelper.CronToHumanReadable(cron),
            nextRunAt = entry.NextRunAt,
        });
    }

    private ToolResult List()
    {
        var jobs = scheduler.GetJobs();
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

        return ToolResult.JsonSuccess(result);
    }

    private ToolResult Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("id is required for delete");

        return scheduler.TryRemoveJob(id)
            ? ToolResult.JsonSuccess(new { id, status = "deleted" })
            : ToolResult.Error($"Cron job '{id}' not found");
    }

    private async Task<ToolResult> PauseAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("id is required for pause");

        var ok = await scheduler.TrySetPausedAsync(id, paused: true, ct).ConfigureAwait(false);
        return ok
            ? ToolResult.JsonSuccess(new { id, status = "paused" })
            : ToolResult.Error($"Cron job '{id}' not found");
    }

    private async Task<ToolResult> ResumeAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("id is required for resume");

        var ok = await scheduler.TrySetPausedAsync(id, paused: false, ct).ConfigureAwait(false);
        return ok
            ? ToolResult.JsonSuccess(new { id, status = "resumed" })
            : ToolResult.Error($"Cron job '{id}' not found");
    }
}
