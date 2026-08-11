// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Context;
using System.Text;

namespace OneCode.App.Services.GoalMode;

/// <summary>
/// Goal 模式上下文提供者——基于 MAF <see cref="AIContextProvider"/> 实现。
///
/// 解决子目标间信息断裂与无上下文注入问题：
/// 把当前 GoalPlan 进度、已完成子目标的输出摘要注入到 LLM context，
/// 让后续子目标能看到前面的工作成果，避免重复工作或冲突编辑。
///
/// 注入策略（仅 Goal 模式）：
/// - 当前子目标位置 + 总数
/// - 已完成子目标的简短摘要（截断 lastOutput 避免上下文爆炸）
/// - 已失败子目标的原因（gapAnalysis）
///
/// 与 BuildModeAttachmentProvider 对齐：gated on PermissionMode 内部判断，
/// 非 Goal 模式时 no-op。
/// </summary>
public sealed class GoalContextProvider : ReadOnlyAIContextProviderBase
{
    private readonly IPermissionModeProvider _modeProvider;
    private readonly GoalContextState _state;

    public GoalContextProvider(IPermissionModeProvider modeProvider, GoalContextState state)
    {
        _modeProvider = modeProvider;
        _state = state;
    }

    private bool IsInGoalMode => _modeProvider.CurrentMode == PermissionMode.GoalAuto;

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        if (!IsInGoalMode) return ValueTask.FromResult(new AIContext());

        var snapshot = _state.Snapshot;
        if (snapshot is null) return ValueTask.FromResult(new AIContext());

        var message = BuildContextMessage(snapshot);
        if (string.IsNullOrEmpty(message))
            return ValueTask.FromResult(new AIContext());

        return ValueTask.FromResult(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, message)],
        });
    }

    private static string BuildContextMessage(GoalContextSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[GOAL MODE CONTEXT]");
        var depthHint = snap.CurrentGoalDepth > 0 ? $" (depth={snap.CurrentGoalDepth}, recursively decomposed)" : "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"You are executing sub-goal {snap.CurrentGoalId}{depthHint} of {snap.TotalGoals}.");
        sb.AppendLine();

        if (snap.CompletedSummaries.Count > 0)
        {
            sb.AppendLine("Previously completed sub-goals (build on these results, do NOT repeat work):");
            foreach (var (id, description, summary) in snap.CompletedSummaries)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ✓ #{id}: {description}");
                if (!string.IsNullOrEmpty(summary))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    Output: {summary}");
            }
            sb.AppendLine();
        }

        if (snap.FailedSummaries.Count > 0)
        {
            sb.AppendLine("Previously failed sub-goals (avoid repeating the same mistakes):");
            foreach (var (id, description, gap) in snap.FailedSummaries)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ✗ #{id}: {description}");
                if (!string.IsNullOrEmpty(gap))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    Gap: {gap}");
            }
            sb.AppendLine();
        }

        if (snap.SharedTransactionHint)
        {
            sb.AppendLine("NOTE: All sub-goals share the same edit transaction. Files edited in earlier sub-goals are already modified — verify current state before editing.");
        }

        return sb.ToString();
    }
}

/// <summary>
/// 共享的 Goal 模式上下文状态。由 Goal 工作流 Runtime 在每个子目标执行前后更新，
/// 由 GoalContextProvider 在每个 turn 读取。基于 AsyncLocal 按异步执行流隔离，
/// 并发 Goal 会话互不污染；所有读取返回不可变快照。
/// </summary>
public sealed class GoalContextState
{
    private readonly AsyncLocal<GoalContextSnapshot?> _current = new();

    public GoalContextSnapshot? Snapshot => _current.Value;

    public void Update(GoalContextSnapshot snapshot)
        => _current.Value = snapshot;

    public void Clear()
        => _current.Value = null;

    public IDisposable Push(GoalContextSnapshot snapshot)
    {
        var previous = _current.Value;
        _current.Value = snapshot;
        return new Scope(this, previous);
    }

    private sealed class Scope(GoalContextState owner, GoalContextSnapshot? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._current.Value = previous;
        }
    }
}

/// <summary>
/// 不可变快照，记录 Goal 模式当前进度。
/// </summary>
/// <param name="CurrentGoalDepth">
/// 当前子目标的递归分解深度（0 = 根子目标，1+ = 按需分解的子目标）。
/// 让 LLM 知道当前是哪一层，对理解父子关系和已分解上下文有帮助。
/// </param>
public sealed record GoalContextSnapshot(
    int CurrentGoalId,
    int TotalGoals,
    IReadOnlyList<(int Id, string Description, string Summary)> CompletedSummaries,
    IReadOnlyList<(int Id, string Description, string Gap)> FailedSummaries,
    bool SharedTransactionHint,
    int CurrentGoalDepth = 0);
