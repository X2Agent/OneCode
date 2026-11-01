using System.Text;
using OneCode.App.Services.Compact;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.Core.Models;

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
    ITokenUsageTracker tokenUsageTracker,
    IModelManager modelManager,
    IGoalRunApplicationService goalRunService,
    ILogger<StatusCommand>? logger = null) : Command
{
    public override string Name => "status";
    public override string Description => "Show runtime status, usage stats, or context window";
    public override CommandCategory Category => CommandCategory.Diagnostic;
    public override string? ArgumentHint => "[info|stats|window]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
        return sub switch
        {
            "info" => await ShowInfoAsync(ct).ConfigureAwait(false),
            "stats" => ShowStats(),
            "window" => ShowWindow(),
            _ => CommandResult.Error($"Unknown subcommand: {sub}. Use: info, stats, window"),
        };
    }

    private async Task<CommandResult> ShowInfoAsync(CancellationToken ct)
    {
        var conv = sessionManager.ForegroundConversation;
        var state = appState.Current;

        var sb = new StringBuilder("Runtime Status:");

        // Session identity — lifecycle ops remain on /session.
        if (conv is null)
        {
            sb.AppendLine("  Session:     (none)");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Session:     {conv.Id}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  标题:        {conv.DisplayTitle() ?? "(未命名)"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Conv status: {conv.Status}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Background:  {sessionManager.BackgroundSessionCount}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Created:     {conv.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Activity:    {conv.LastActivityAt:yyyy-MM-dd HH:mm:ss}");
        }

        // 当前会话的 Goal run 隔离 worktree——持久查询出口，避免启动 notice 滚屏后不可追溯。
        if (conv is not null)
        {
            var goalRun = await goalRunService.GetBySessionAsync(conv.Id, ct).ConfigureAwait(false);
            if (goalRun?.Workspace is { } workspace)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  Goal worktree: {workspace.IsolatedPath} (branch: {workspace.WorktreeBranch}, state: {goalRun.State})");
            }
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

        var sb = new StringBuilder();
        sb.AppendLine("Session Statistics:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Messages:        {conv?.Messages.Count ?? 0}");

        // tokenUsageTracker 可能为 null（如 SessionManager 内部降级路径），此时从 Conversation.TotalUsage 读取基本统计
        if (tokenUsageTracker is not null)
        {
            if (tokenUsageTracker.QueryCount > 0)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  LLM queries:     {tokenUsageTracker.QueryCount}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Input tokens:    {tokenUsageTracker.TotalInputTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Output tokens:   {tokenUsageTracker.TotalOutputTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Cache read:      {tokenUsageTracker.TotalCacheReadTokens:N0}  ({tokenUsageTracker.CacheHitRate:P1} hit rate)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Cache write:     {tokenUsageTracker.TotalCacheWriteTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Total tokens:    {tokenUsageTracker.TotalAllTokens:N0}");
            // 口径标注：stats 读 ledger 会话桶（API usage 原始口径、水合跨重启累计）。
            // Total = Input + Output + CacheWrite——MEAI 契约下 Input 已含缓存读，不得再加一遍；
            // window 子命令的占用口径是最近一次请求的实际输入；预算熔断另用 billable 折算（缓存读×0.1）。
            sb.AppendLine("  Caliber:         raw API usage (ledger session bucket, input includes cache read)");

            if (tokenUsageTracker.LastBreakdown is { } breakdown)
            {
                sb.AppendLine();
                sb.AppendLine("  Token Breakdown (last query, estimated):");
                AppendBreakdown(sb, breakdown, includeSystemDetail: true);
            }

            if (tokenUsageTracker.CumulativeBreakdown is { } cumulative)
            {
                sb.AppendLine();
                sb.AppendLine("  Token Breakdown (cumulative, estimated):");
                AppendBreakdown(sb, cumulative, includeSystemDetail: false);
            }
        }
        else
        {
            // 降级：仅从 Conversation.TotalUsage 读取基本统计，无 query count / cache hit / breakdown
            var usage = conv?.TotalUsage;
            var inputTokens = usage?.InputTokens ?? 0;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Input tokens:    {inputTokens:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Output tokens:   {usage?.OutputTokens ?? 0:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  Total tokens:    {inputTokens + (usage?.OutputTokens ?? 0) + (usage?.CacheWriteTokens ?? 0):N0}");
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    /// <summary>渲染一段分场景估算：system 细分仅最近查询段展示，累计段只给大项。</summary>
    private static void AppendBreakdown(StringBuilder sb, TokenBreakdown breakdown, bool includeSystemDetail)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"    System prompt:  {breakdown.SystemPrompt:N0}");
        if (includeSystemDetail && breakdown.SystemPromptDetail is { } detail)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Template:      {detail.TemplateBody:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Environment:   {detail.Environment:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Project ctx:   {detail.ProjectContext:N0}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Memory:        {detail.Memory:N0}");
            if (detail.OtherSections > 0)
                sb.AppendLine(CultureInfo.InvariantCulture, $"      ├ Other sections:{detail.OtherSections:N0}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    Tools:          {breakdown.Tools:N0} (product {breakdown.ProductTools:N0} | mcp {breakdown.McpTools:N0})");

        // 消息级 provider 注入的逐贡献者分桶（skills / design 经 instructions 合并，归入 System prompt）
        if (breakdown.ContextProviders.Count > 0)
        {
            sb.AppendLine("    Context providers:");
            foreach (var (sourceId, tokens) in breakdown.ContextProviders.OrderByDescending(kv => kv.Value))
                sb.AppendLine(CultureInfo.InvariantCulture, $"      {FriendlyProviderName(sourceId)}: {tokens:N0}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"    Messages:       {breakdown.Messages:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    Other context:  {breakdown.Other:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    ───────────────");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    Total:          {breakdown.TotalEstimated:N0}");
    }

    /// <summary>sourceId（provider 类型全名，嵌套类型以 '+' 分隔）→ 末段短名。</summary>
    private static string FriendlyProviderName(string sourceId) => sourceId.Split('.', '+')[^1];

    private CommandResult ShowWindow()
    {
        var conv = sessionManager.ForegroundConversation;
        var usage = conv?.TotalUsage;
        var model = appState.Current.MainLoopModel ?? "unknown";

        // 通过 TokenBudget.GetMaxContextTokens 统一解析上下文窗口，
        // 走 ModelManager 配置 → ModelCatalog 快照 → 前缀匹配 → 默认值 四层兜底，
        // 与 /status stats 和 AutoCompactService 使用同一数据源。
        var maxTokens = TokenBudget.GetMaxContextTokens(model, modelManager);

        // 占用口径：最近一次模型调用的实际输入（tracker 由 QueryStreamEngine 每次查询后回填）。
        // 会话累计消费（input+output）随轮数线性增长，不能当「窗口占用」——长会话必然爆表。
        var occupancy = tokenUsageTracker?.LastTurnInputTokens ?? 0;
        var pct = maxTokens > 0 ? (double)occupancy / maxTokens : 0;
        const int barLen = 30;
        var filled = Math.Min((int)(pct * barLen), barLen);
        var bar = new string('█', filled) + new string('░', barLen - filled);

        var cumulative = (usage?.InputTokens ?? 0) + (usage?.OutputTokens ?? 0);

        return CommandResult.Text($"""
            Context Window:
              Model:      {model}
              Occupancy:  {occupancy:N0} / {maxTokens:N0} tokens ({pct:P0})
              [{bar}]
              Messages:   {conv?.Messages.Count ?? 0}
              Cumulative: {cumulative:N0} tokens billed across all turns (raw input+output)
              Caliber:    occupancy = last request actual input; AutoCompact 70% warning uses transcript estimate (TokenBudget.Estimate)
            """);
    }
}
