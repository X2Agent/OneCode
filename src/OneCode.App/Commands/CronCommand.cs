using System.Text;
using OneCode.Automation.Cron;
using OneCode.Core.Cron;

namespace OneCode.App.Commands;

/// <summary>
/// Slash command for managing cron jobs from the TUI. Subcommands:
///   /cron                       — list all jobs (default)
///   /cron list                  — same as above
///   /cron create &lt;cron&gt; &lt;prompt&gt; [--once] [--durable]
///   /cron delete &lt;id&gt;
///   /cron pause &lt;id&gt;
///   /cron resume &lt;id&gt;
///   /cron run &lt;id&gt;             — fire the job's prompt immediately (bypasses NextRunAt)
/// </summary>
public sealed class CronCommand(
    CronSchedulerService scheduler,
    ICronParser cronParser,
    ICronJobExecutor executor) : Command
{
    public override string Name => "cron";
    public override string Description => "Manage scheduled cron jobs";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|create|delete|pause|resume|run]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Text(RenderList());

        return args[0].ToLowerInvariant() switch
        {
            "create" or "add" => await CreateAsync(args, ct).ConfigureAwait(false),
            "delete" or "remove" or "rm" => Delete(args),
            "pause" => await PauseAsync(args, ct).ConfigureAwait(false),
            "resume" => await ResumeAsync(args, ct).ConfigureAwait(false),
            "run" or "trigger" => await RunAsync(args, ct).ConfigureAwait(false),
            _ => CommandResult.Error("Usage: /cron [list|create|delete|pause|resume|run]"),
        };
    }

    private string RenderList()
    {
        var jobs = scheduler.GetJobs();
        var sb = new StringBuilder($"Cron jobs ({jobs.Count}):\n");
        if (jobs.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var j in jobs)
            {
                var preview = j.Prompt.Length > 60 ? j.Prompt[..60] + "..." : j.Prompt;
                var schedule = CronExpressionHelper.CronToHumanReadable(j.Cron);
                var state = j.Paused ? "[PAUSED]" : j.Recurring ? "[recurring]" : "[one-shot]";
                var durable = j.Durable ? " durable" : "";
                var next = j.NextRunAt is long ts
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "-";
                var last = j.LastRunAt is long lts
                    ? DateTimeOffset.FromUnixTimeSeconds(lts).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "-";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {j.Id} {state}{durable} {j.Cron} ({schedule})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"      prompt: {preview}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"      next: {next}  last: {last}");
            }
        }
        sb.AppendLine("\nUsage: /cron create <cron> <prompt> [--once] [--durable] | delete <id> | pause <id> | resume <id> | run <id>");
        return sb.ToString().TrimEnd();
    }

    private async Task<CommandResult> CreateAsync(string[] args, CancellationToken ct)
    {
        // Parse: /cron create <cron> <prompt> [--once] [--durable]
        // The cron expression is the first positional arg; the prompt is the rest joined.
        // Flags --once and --durable may appear anywhere after the cron expression.
        if (args.Length < 3)
            return CommandResult.Error("Usage: /cron create <cron-expr> <prompt> [--once] [--durable]");

        var cron = args[1];
        if (!cronParser.IsValid(cron))
            return CommandResult.Error($"Invalid cron expression: {cron}");

        var recurring = !args.Contains("--once", StringComparer.OrdinalIgnoreCase);
        var durable = args.Contains("--durable", StringComparer.OrdinalIgnoreCase)
                      && CronPaths.IsDurableCronEnabled();
        if (args.Contains("--durable", StringComparer.OrdinalIgnoreCase) && !CronPaths.IsDurableCronEnabled())
            return CommandResult.Error("--durable requires ONECODE_DURABLE_CRON=true in the environment.");

        // Compose prompt from positional args (skip "create", the cron expr, and any flag tokens).
        var promptTokens = args.Skip(2)
            .Where(a => !a.StartsWith("--", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (promptTokens.Length == 0)
            return CommandResult.Error("Prompt is required.");
        var prompt = string.Join(' ', promptTokens);

        var nextRun = cronParser.ComputeNextRun(cron, DateTimeOffset.UtcNow);
        if (nextRun is null)
            return CommandResult.Error($"No future matches for cron expression: {cron}");

        var entry = new CronJobEntry
        {
            Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8],
            Cron = cron,
            Prompt = prompt,
            Recurring = recurring,
            Durable = durable,
            Paused = false,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            NextRunAt = nextRun.Value.ToUnixTimeSeconds(),
        };

        if (!await scheduler.TryAddJobAsync(entry, ct).ConfigureAwait(false))
            return CommandResult.Error(
                $"Max {CronSchedulerService.MaxJobs} cron jobs reached or job could not be persisted.");

        var schedule = CronExpressionHelper.CronToHumanReadable(cron);
        var next = DateTimeOffset.FromUnixTimeSeconds(entry.NextRunAt!.Value).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return CommandResult.Text(
            $"Created cron job {entry.Id} ({schedule}){(durable ? " [durable]" : "")}.\n" +
            $"  prompt: {prompt}\n" +
            $"  next run: {next}");
    }

    private CommandResult Delete(string[] args)
    {
        if (args.Length < 2) return CommandResult.Error("Usage: /cron delete <id>");
        var id = args[1];
        return scheduler.TryRemoveJob(id)
            ? CommandResult.Text($"Deleted cron job {id}.")
            : CommandResult.Error($"Cron job '{id}' not found.");
    }

    private async Task<CommandResult> PauseAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2) return CommandResult.Error("Usage: /cron pause <id>");
        var id = args[1];
        return await scheduler.TrySetPausedAsync(id, paused: true, ct).ConfigureAwait(false)
            ? CommandResult.Text($"Paused cron job {id}.")
            : CommandResult.Error($"Cron job '{id}' not found.");
    }

    private async Task<CommandResult> ResumeAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2) return CommandResult.Error("Usage: /cron resume <id>");
        var id = args[1];
        return await scheduler.TrySetPausedAsync(id, paused: false, ct).ConfigureAwait(false)
            ? CommandResult.Text($"Resumed cron job {id}.")
            : CommandResult.Error($"Cron job '{id}' not found.");
    }

    private async Task<CommandResult> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2) return CommandResult.Error("Usage: /cron run <id>");
        var id = args[1];

        var job = scheduler.GetJobs().FirstOrDefault(j =>
            string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
        if (job is null) return CommandResult.Error($"Cron job '{id}' not found.");

        try
        {
            await executor.ExecuteJobAsync(job.Prompt, ct).ConfigureAwait(false);
            return CommandResult.Text($"Triggered cron job {id}.");
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to trigger cron job {id}: {ex.Message}");
        }
    }
}
