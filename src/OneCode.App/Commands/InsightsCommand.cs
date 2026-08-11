using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using System.Text;
using CoreConstants = OneCode.Core.Constants;
namespace OneCode.App.Commands;

public sealed class InsightsCommand(ILogger<InsightsCommand>? logger = null) : Command
{
    public override string Name => "insights";
    public override string Description => "Analyze usage patterns across saved sessions";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ProgressMessage => "analyzing your sessions";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var home = PathsHelper.UserHome;
        var sessionsDir = Path.Combine(home, Constants.App.ConfigDirName, "sessions");

        string[] files = [];
        if (Directory.Exists(sessionsDir))
        {
            files = Directory.GetFiles(sessionsDir, "*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTime).Take(100).ToArray();
        }

        if (files.Length == 0)
            return CommandResult.Text("No sessions to analyze.");

        var totalSessions = files.Length;
        var totalMessages = 0;
        long totalInputTokens = 0, totalOutputTokens = 0;
        var modelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skippedLines = 0;
        var skippedFiles = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
                bool usedHeaderTokens = false;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        // First line is always session_header: extract aggregate totals + model.
                        if (root.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "session_header")
                        {
                            if (root.TryGetProperty("total_usage", out var usage))
                            {
                                if (usage.TryGetProperty("input_tokens", out var it))
                                    totalInputTokens += it.GetInt64();
                                if (usage.TryGetProperty("output_tokens", out var ot))
                                    totalOutputTokens += ot.GetInt64();
                                usedHeaderTokens = true;
                            }
                            if (root.TryGetProperty("model", out var m) && m.GetString() is { } model && !string.IsNullOrEmpty(model))
                                modelCounts[model] = modelCounts.GetValueOrDefault(model) + 1;
                            continue;
                        }

                        // Count only actual conversation turns (user/assistant messages).
                        if (root.TryGetProperty("role", out var role))
                        {
                            var roleStr = role.GetString();
                            if (roleStr is CoreConstants.MessageTypes.User or CoreConstants.MessageTypes.Assistant)
                            {
                                totalMessages++;

                                // Accumulate per-message token usage if header didn't have totals.
                                if (!usedHeaderTokens && root.TryGetProperty("token_usage", out var tu))
                                {
                                    if (tu.TryGetProperty("input_tokens", out var it2)) totalInputTokens += it2.GetInt64();
                                    if (tu.TryGetProperty("output_tokens", out var ot2)) totalOutputTokens += ot2.GetInt64();
                                }
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        // 损坏的会话行：计数并留痕，不再静默跳过——否则统计偏低且用户无感知。
                        skippedLines++;
                        logger?.LogDebug(ex, "Skipping malformed session line in {File}", file);
                    }
                }
            }
            catch (IOException ex)
            {
                skippedFiles++;
                logger?.LogWarning(ex, "Skipping unreadable session file {File}", file);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Session Insights (last {totalSessions} sessions):");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Total messages:      {totalMessages:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Total input tokens:  {totalInputTokens:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Total output tokens: {totalOutputTokens:N0}");
        if (modelCounts.Count > 0)
        {
            sb.AppendLine("\n  Models used:");
            foreach (var (model, count) in modelCounts.OrderByDescending(kv => kv.Value))
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {model,-40} {count:N0} sessions");
        }

        if (skippedLines > 0 || skippedFiles > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"\n  ⚠ Data quality: skipped {skippedLines} malformed line(s) and {skippedFiles} unreadable file(s) — totals may be understated.");
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }
}
