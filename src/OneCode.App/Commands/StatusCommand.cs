using System.Text;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.Core.Models;
using OneCode.Core.Cost;

namespace OneCode.App.Commands;

/// <summary>
/// /status — runtime diagnostics for the active session.
/// Session lifecycle (list / new / switch / close) lives on <c>/session</c>.
///
/// Subcommands:
///   /status          → identity + runtime state (default)
///   /status info     → same as default
///   /status stats    → token usage, cache hit rate, and per-scenario breakdown
///   /status window   → context window usage with progress bar
/// </summary>
public sealed class StatusCommand(
    ISessionManager sessionManager,
    IAppStateAccessor appState,
    IPermissionModeProvider modeProvider,
    ICostTracker costTracker,
    ITokenUsageTracker tokenUsageTracker,
    IModelManager modelManager,
    ILogger<StatusCommand>? logger = null) : Command
{
    public override string Name => "status";
    public override string Description => "Show runtime status, usage stats, or context window";
    public override CommandCategory Category => CommandCategory.Diagnostic;
    public override string? ArgumentHint => "[info|stats|window]";

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
        return Task.FromResult(sub switch
        {
            "info" => ShowInfo(),
            "stats" => ShowStats(),
            "window" => ShowWindow(),
            _ => CommandResult.Error($"Unknown subcommand: {sub}. Use: info, stats, window"),
        });
    }

    private CommandResult ShowInfo()
    {
        var conv = sessionManager.ForegroundConversation;
        var state = appState.Current;

        var sb = new StringBuilder("Runtime Status:");

        // Session identity (formerly /session info) — lifecycle ops remain on /session.
        if (conv is null)
        {
            sb.AppendLine("  Session:     (none)");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Session:     {conv.Id}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Name:        {conv.Name}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Conv status: {conv.Status}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Background:  {sessionManager.BackgroundSessionCount}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Created:     {conv.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Activity:    {conv.LastActivityAt:yyyy-MM-dd HH:mm:ss}");
            if (conv.Branch is not null)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Conv branch: {conv.Branch}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"  Model:       {state.MainLoopModel ?? "(default)"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Messages:    {conv?.Messages.Count ?? 0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Permissions: {modeProvider.CurrentMode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Thinking:    {(state.ThinkingEnabled ? "ON" : "OFF")} (effort: {state.EffortValue.ToString().ToLowerInvariant()})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  ShowThinking:{(state.ShowThinking ? "ON" : "OFF")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Tools:       {state.Tools.Count}");

        try
        {
            // Note: synchronous blocking here is acceptable — this is a CLI status
            // command, not on a hot path. The file read is tiny (git HEAD is ~40 bytes).
            var gitHead = File.ReadAllText(
                Path.Combine(Directory.GetCurrentDirectory(), ".git", "HEAD"));
            var trimmed = gitHead.Trim();
            if (trimmed.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Git branch:  {trimmed["ref: refs/heads/".Length..]}");
        }
        catch (IOException ex)
        {
            if (logger is not null)
                logger.LogDebug(ex, "StatusCommand: failed to read .git/HEAD");
            else
                System.Diagnostics.Debug.WriteLine($"StatusCommand failed to read .git/HEAD: {ex.Message}");
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private CommandResult ShowStats()
    {
        var conv = sessionManager.ForegroundConversation;
        var totalCost = costTracker.GetTotalCost();

        var sb = new StringBuilder();
        sb.AppendLine("Session Statistics:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Messages:        {conv?.Messages.Count ?? 0}");

        // tokenUsageTracker 可能为 null（如 SessionManager 内部降级路径），此时从 Conversation.TotalUsage 读取基本统计
        if (tokenUsageTracker is not null)
        {
            var inputTokens = tokenUsageTracker.TotalInputTokens;
            var outputTokens = tokenUsageTracker.TotalOutputTokens;
            var cacheReadTokens = tokenUsageTracker.TotalCacheReadTokens;
            var cacheWriteTokens = tokenUsageTracker.TotalCacheWriteTokens;
            var queryCount = tokenUsageTracker.QueryCount;
            var cacheHitRate = tokenUsageTracker.CacheHitRate;
            var breakdown = tokenUsageTracker.LastBreakdown;

            if (queryCount > 0)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  LLM queries:     {queryCount}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Input tokens:    {inputTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Output tokens:   {outputTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Cache read:      {cacheReadTokens:N0}  ({cacheHitRate:P1} hit rate)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Cache write:     {cacheWriteTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Total tokens:    {inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens:N0}");

            // 分场景 token 估算（来自 TokenBreakdownEstimator）
            if (breakdown is not null)
            {
                sb.AppendLine();
                sb.AppendLine("  Token Breakdown (last query, estimated):");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    System prompt:  {breakdown.SystemPrompt:N0}");
                // 系统提示词内部分场景（按 markdown 标题切分）
                if (breakdown.SystemPromptDetail is { } detail)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Template:      {detail.TemplateBody:N0}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Environment:   {detail.Environment:N0}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Project ctx:   {detail.ProjectContext:N0}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Memory:        {detail.Memory:N0}");
                    if (detail.OtherSections > 0)
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Other sections:{detail.OtherSections:N0}");
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Tools & skills: {breakdown.ToolsAndSkills:N0}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Messages:       {breakdown.Messages:N0}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Other context:  {breakdown.Other:N0}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    ───────────────");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    Total:          {breakdown.TotalEstimated:N0}");
            }
        }
        else
        {
            // 降级：仅从 Conversation.TotalUsage 读取基本统计，无 query count / cache hit / breakdown
            var usage = conv?.TotalUsage;
            var inputTokens = usage?.InputTokens ?? 0;
            var outputTokens = usage?.OutputTokens ?? 0;
            var cacheReadTokens = usage?.CacheReadTokens ?? 0;
            var cacheWriteTokens = usage?.CacheWriteTokens ?? 0;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Input tokens:    {inputTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Output tokens:   {outputTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Total tokens:    {inputTokens + outputTokens + cacheReadTokens + cacheWriteTokens:N0}");
        }

        var costStr = totalCost > 0 ? $"${totalCost:F4}" : "n/a";
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Estimated cost:  {costStr}");

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private CommandResult ShowWindow()
    {
        var conv = sessionManager.ForegroundConversation;
        var usage = conv?.TotalUsage;
        var model = appState.Current.MainLoopModel ?? "unknown";

        // 通过 TokenBudget.GetMaxContextTokens 统一解析上下文窗口，
        // 走 ModelManager 配置 → ModelCatalog 快照 → 前缀匹配 → 默认值 四层兜底，
        // 与 /status stats 和 AutoCompactService 使用同一数据源。
        var maxTokens = TokenBudget.GetMaxContextTokens(model, modelManager);

        // 环境变量 ONECODE_MAX_CONTEXT_TOKENS 仍可作为最终覆盖手段保留
        if (int.TryParse(
            Environment.GetEnvironmentVariable("ONECODE_MAX_CONTEXT_TOKENS"), out var envMax) && envMax > 0)
            maxTokens = envMax;

        var usedTokens = (usage?.InputTokens ?? 0) + (usage?.OutputTokens ?? 0);
        var pct = maxTokens > 0 ? (double)usedTokens / maxTokens : 0;
        var barLen = 30;
        var filled = (int)(pct * barLen);
        var bar = new string('█', Math.Min(filled, barLen)) + new string('░', Math.Max(barLen - filled, 0));

        return CommandResult.Text($"""
            Context Window:
              Model:     {model}
              Used:      {usedTokens:N0} / {maxTokens:N0} tokens ({pct:P0})
              [{bar}]
              Messages:  {conv?.Messages.Count ?? 0}
            """);
    }
}
