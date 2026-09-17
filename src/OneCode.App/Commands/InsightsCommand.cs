using OneCode.Core.Session;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using System.Text;
namespace OneCode.App.Commands;

/// <summary>
/// 汇总 <c>~/.onecode/events/*.jsonl</c> 中的会话事件，输出用量统计。
/// </summary>
/// <param name="logger">可选日志。</param>
/// <param name="userHomeOverride">
/// 仅单元测试使用的用户主目录接缝；null 时取 <see cref="PathsHelper.UserHome"/>。
/// 读取端必须与 <c>FileSessionEventStore</c> 的写入端（同样以用户主目录为基准）保持一致，
/// 该参数使测试能用真实事件存储写出数据后验证解析契约。
/// </param>
public sealed class InsightsCommand(
    ILogger<InsightsCommand>? logger = null,
    string? userHomeOverride = null) : Command
{
    public override string Name => "insights";
    public override string Description => "Analyze usage patterns across saved sessions";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ProgressMessage => "analyzing your sessions";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var home = userHomeOverride ?? PathsHelper.UserHome;
        var sessionsDir = Path.Combine(home, Constants.App.ConfigDirName, Constants.Subdirs.Events);

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

                // 同一会话文件可含多个 SessionStarted / SessionSnapshot（快照变更时追加），
                // Conversation.TotalUsage 是累计值——取序号最大的一条，避免逐条累加导致重复计数。
                long latestSnapshotSequence = -1;
                long snapshotInputTokens = 0, snapshotOutputTokens = 0;
                string? snapshotModel = null;
                var fileHasSnapshot = false;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("type", out var typeProp) ||
                            typeProp.GetString() is not { } eventType)
                        {
                            continue;
                        }

                        // 会话元数据事件：session_started / session_snapshot
                        if (eventType is SessionEventTypes.Started or SessionEventTypes.Snapshot)
                        {
                            if (!root.TryGetProperty("payload", out var payload) ||
                                payload.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var sequence = root.TryGetProperty("sequence", out var seqProp) &&
                                           seqProp.TryGetInt64(out var seq)
                                ? seq
                                : 0L;

                            if (sequence < latestSnapshotSequence)
                                continue;

                            latestSnapshotSequence = sequence;
                            fileHasSnapshot = true;

                            snapshotInputTokens = 0;
                            snapshotOutputTokens = 0;
                            snapshotModel = null;

                            if (payload.TryGetProperty("total_usage", out var usage) &&
                                usage.ValueKind == JsonValueKind.Object)
                            {
                                if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv))
                                    snapshotInputTokens = iv;
                                if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov))
                                    snapshotOutputTokens = ov;
                            }

                            if (payload.TryGetProperty("model", out var m) &&
                                m.ValueKind == JsonValueKind.String)
                            {
                                snapshotModel = m.GetString();
                            }

                            continue;
                        }

                        // 只统计真实对话轮次（用户/助手消息事件）
                        if (eventType is SessionEventTypes.UserMessage or SessionEventTypes.AssistantMessage)
                            totalMessages++;
                    }
                    catch (JsonException ex)
                    {
                        // 损坏的会话行：计数并留痕，不再静默跳过——否则统计偏低且用户无感知。
                        skippedLines++;
                        logger?.LogDebug(ex, "Skipping malformed session line in {File}", file);
                    }
                }

                totalInputTokens += snapshotInputTokens;
                totalOutputTokens += snapshotOutputTokens;

                if (fileHasSnapshot && !string.IsNullOrEmpty(snapshotModel))
                    modelCounts[snapshotModel] = modelCounts.GetValueOrDefault(snapshotModel) + 1;
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
