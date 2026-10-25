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
    /// next process start can resume it.
    /// </summary>
    public bool Durable { get; set; }

    /// <summary>
    /// When <c>true</c> the scheduler skips this job until <c>false</c> again. Unlike
    /// deletion, pausing preserves <see cref="NextRunAt"/>/<see cref="LastRunAt"/> history
    /// and keeps the job's JSON on disk. Recurring jobs recomputed <see cref="NextRunAt"/>
    /// from "now + 1s" on resume so they don't fire a backlog of missed runs.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// 作业归属的项目目录（绝对路径），创建时由调度器以进程工作目录盖章。
    /// durable 作业只被项目匹配的调度器触发；<c>null</c>（旧版 durable JSON）保持
    /// 「任意进程都可触发」的历史行为。非 durable 作业不出创建进程，该字段仅作展示。
    /// </summary>
    public string? ProjectDir { get; set; }

    public long CreatedAt { get; set; }
    public long? LastRunAt { get; set; }
    public long? NextRunAt { get; set; }

    /// <summary>最近一次触发的执行状态（"ok" 或 "error"）。</summary>
    public string? LastStatus { get; set; }

    /// <summary>最近一次触发失败时的错误摘要（成功时为 null）。</summary>
    public string? LastError { get; set; }
}
