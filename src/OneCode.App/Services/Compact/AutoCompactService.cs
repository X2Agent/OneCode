using OneCode.App.Session;

namespace OneCode.App.Services.Compact;

/// <summary>
/// 自动压缩监控服务：在 token 使用率超过 70% 时发射告警，提醒用户执行 /compact。
///
/// <para><b>架构变更</b>：实际压缩由 MAF <c>CompactionProvider</c> 在 pipeline 内自动完成
/// （L0 去重 → L1 ToolResult 折叠 → L2 LLM 摘要 → L3 截断），本类不再执行任何压缩动作。
/// 保留的职责仅剩 70% 告警——这是 MAF 没有的能力（MAF 只在 token 超阈值时静默压缩，
/// 不会提前提醒用户）。</para>
///
/// <para><b>运行时集成点</b>：<see cref="Streaming.QueryStreamService"/> 在每次 agent turn 结束后
/// 调用 <see cref="CheckAndWarnAsync"/>，通过 <see cref="ConsumeWarning"/> 发射 <c>TuiCompactSuggested</c> 事件。</para>
///
/// <para><b>阈值即语义</b>：
///   0.70 (<see cref="WarningThreshold"/>) — 首次跨越时发射告警，提醒用户执行 /compact（无 LLM，无压缩）
///   0.85+ — MAF in-pipeline 自动压缩（L1 ToolResult 折叠），用户无感知
///   0.95+ — MAF in-pipeline LLM 摘要 + 截断兜底，用户无感知
/// </para>
/// </summary>
public sealed class AutoCompactService
{
    private const double WarningThreshold = 0.70;
    private const int MaxTrackedSessions = 100;

    private static bool IsRunningAsWorkerAgent() =>
        Environment.GetEnvironmentVariable("ONECODE_IS_WORKER") is "1" or "true";

    private readonly CompactService _compactService;
    private readonly ISessionConversationAccess _sessionManager;
    private readonly ILogger<AutoCompactService> _logger;
    private readonly Dictionary<string, CompactWarningState> _warningStates = new();
    private readonly object _warningLock = new();

    public AutoCompactService(
        CompactService compactService,
        ISessionConversationAccess sessionManager,
        ILogger<AutoCompactService> logger)
    {
        _compactService = compactService;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// 检查 token 使用率并在 70% 阈值时设置告警标志。
    /// 实际压缩由 MAF CompactionProvider 在 pipeline 内自动完成，本方法不触发任何压缩动作。
    /// </summary>
    public async Task CheckAndWarnAsync(string? systemPrompt = null, CancellationToken ct = default)
    {
        var session = _sessionManager.ForegroundConversation;
        if (session is not null)
            await CheckAndWarnAsync(session, systemPrompt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 检查指定会话的 token 使用率并在 70% 阈值时设置告警标志。
    /// </summary>
    public Task CheckAndWarnAsync(
        Conversation session,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        if (!IsRunningAsWorkerAgent())
        {
            var budgetStatus = _compactService.GetBudgetStatus(session, systemPrompt);
            CheckAndWarn(session, budgetStatus.UsageRatio);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 消费未读的 70% 告警。若存在首次跨越 <see cref="WarningThreshold"/> 的告警，
    /// 返回 true 并清除标志。调用方（QueryStreamService）据此发射 <c>TuiCompactSuggested</c> 事件，
    /// 提醒用户执行 /compact。降回阈值以下后标志重置，可再次告警。
    /// </summary>
    public bool ConsumeWarning(SessionId? sessionId = null)
    {
        var id = sessionId ?? _sessionManager.ForegroundConversation?.Id;
        if (id is null) return false;

        lock (_warningLock)
        {
            if (!_warningStates.TryGetValue(id.Value, out var state) || !state.HasUnconsumedWarning)
                return false;
            state.HasUnconsumedWarning = false;
            return true;
        }
    }

    private void CheckAndWarn(Conversation session, double usageRatio)
    {
        var sessionId = session.Id.ToString();
        lock (_warningLock)
        {
            EvictStaleEntries(_warningStates);

            if (!_warningStates.TryGetValue(sessionId, out var state))
            {
                state = new CompactWarningState();
                _warningStates[sessionId] = state;
            }

            if (usageRatio >= WarningThreshold && !state.HasWarned)
            {
                state.HasWarned = true;
                state.HasUnconsumedWarning = true;
                state.LastWarningAt = DateTimeOffset.UtcNow;
                state.WarningCount++;
            }

            if (usageRatio < WarningThreshold && state.HasWarned)
            {
                state.HasWarned = false;
                state.HasUnconsumedWarning = false;
            }
        }
    }

    private static void EvictStaleEntries(Dictionary<string, CompactWarningState> states)
    {
        if (states.Count <= MaxTrackedSessions) return;
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        var stale = states
            .Where(kv => kv.Value.LastWarningAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
            states.Remove(key);
    }
}

public sealed class CompactWarningState
{
    public bool HasWarned { get; set; }
    public bool HasUnconsumedWarning { get; set; }
    public DateTimeOffset? LastWarningAt { get; set; }
    public int WarningCount { get; set; }
}
