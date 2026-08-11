namespace OneCode.Core.Cron;

/// <summary>
/// A single cron job entry.
/// </summary>
public sealed class CronJobEntry
{
    public string Id { get; set; } = "";
    public string Cron { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool Recurring { get; set; } = true;

    /// <summary>
    /// Whether this job survives a process restart. When <c>false</c> (default) the job is
    /// in-memory only and is dropped on shutdown/reload. When <c>true</c> the scheduler
    /// persists <c>{Id}.json</c> to <c>~/.onecode/cron/</c> after every state change so the
    /// next process start can resume it. Honoured only when the host process opts in via
    /// <c>ONECODE_DURABLE_CRON=true</c>; otherwise the field is forced to <c>false</c> on
    /// creation and never persisted.
    /// </summary>
    public bool Durable { get; set; }

    /// <summary>
    /// When <c>true</c> the scheduler skips this job until <c>false</c> again. Unlike
    /// deletion, pausing preserves <see cref="NextRunAt"/>/<see cref="LastRunAt"/> history
    /// and keeps the job's JSON on disk. Recurring jobs recomputed <see cref="NextRunAt"/>
    /// from "now + 1s" on resume so they don't fire a backlog of missed runs.
    /// </summary>
    public bool Paused { get; set; }

    public long CreatedAt { get; set; }
    public long? LastRunAt { get; set; }
    public long? NextRunAt { get; set; }
}
