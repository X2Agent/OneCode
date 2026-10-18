using System.Threading.Channels;
using OneCode.App.Query;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Ambient identity for the currently executing main-agent run.
/// Tool methods do not receive the MAF run descriptor directly, so the chat boundary
/// publishes the immutable run ID through AsyncLocal for the duration of the run.
/// </summary>
public static class OneCodeAgentRunContext
{
    private static readonly AsyncLocal<string?> CurrentRunIdSlot = new();
    private static readonly AsyncLocal<string?> CurrentBuildRunIdSlot = new();

    // 子代理进度旁路通道：写入器指向父 run 的原始事件 Channel。与 RunId 同一模式——
    // 引擎在 StartRun 同段设置，fork（AgentTool → ForkedAgentRunner）在同段捕获的
    // ExecutionContext 内读取并上报进度；父 run 收尾后 channel 已完成，TryWrite 是安全 no-op。
    private static readonly AsyncLocal<ChannelWriter<object>?> SubagentEventWriterSlot = new();

    // 持有的是可变引用而非 bool：工具（子执行上下文）里对 AsyncLocal 的 set 不会回流到
    // 外层中间件的上下文，但对同一引用对象的写入两侧都可见。
    private static readonly AsyncLocal<RunSeal?> PlanSubmissionSealSlot = new();

    // 异步作用域级工作目录覆盖（如 Goal 隔离 Worktree、子代理显式目录覆盖）。
    // SessionWorkingDirectoryAccessor 优先读取此值，消除单例工具与管道中间件之间的目录分裂。
    private static readonly AsyncLocal<string?> WorkingDirectoryOverrideSlot = new();

    public static string? CurrentRunId
    {
        get => CurrentRunIdSlot.Value;
        set => CurrentRunIdSlot.Value = value;
    }

    /// <summary>Current persisted BuildRun identity for task isolation and evidence linkage.</summary>
    public static string? CurrentBuildRunId
    {
        get => CurrentBuildRunIdSlot.Value;
        set => CurrentBuildRunIdSlot.Value = value;
    }

    /// <summary>
    /// 父 run 原始事件通道的写入器（子代理进度上报出口）。null = 当前上下文不在
    /// 流式主查询 run 内（受控 Build attempt 路径不保证延续，进度缺失属可接受降级）。
    /// </summary>
    public static ChannelWriter<object>? SubagentEventWriter
    {
        get => SubagentEventWriterSlot.Value;
        set => SubagentEventWriterSlot.Value = value;
    }

    /// <summary>
    /// 本 run 的「计划已提交」封条。SubmitPlan 成功后落封，工具中间件见封即拒绝后续工具调用，
    /// 让模型只能纯文本收尾、run 自然终结。null = 当前上下文不在可封口的 run 内。
    /// </summary>
    public static RunSeal? PlanSubmissionSeal
    {
        get => PlanSubmissionSealSlot.Value;
        set => PlanSubmissionSealSlot.Value = value;
    }

    /// <summary>
    /// 当前异步执行上下文的工作目录覆盖；为 null 时回退到会话级工作目录。
    /// </summary>
    public static string? WorkingDirectoryOverride
    {
        get => WorkingDirectoryOverrideSlot.Value;
        set => WorkingDirectoryOverrideSlot.Value = value;
    }

    /// <summary>
    /// 在当前异步作用域内绑定工作目录覆盖，释放时自动恢复原值。
    /// </summary>
    public static WorkingDirectoryScope UseWorkingDirectory(string? workingDirectory)
    {
        var previous = WorkingDirectoryOverrideSlot.Value;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            WorkingDirectoryOverrideSlot.Value = workingDirectory;
        return new WorkingDirectoryScope(previous);
    }

    public readonly struct WorkingDirectoryScope(string? previous) : IDisposable
    {
        public void Dispose() => WorkingDirectoryOverrideSlot.Value = previous;
    }
}

/// <summary>
/// 统一保存与恢复主代理/受控 attempt 运行期的全部 AsyncLocal ambient 上下文，
/// 消除 <c>QueryStreamEngine</c> 与 <c>ControlledBuildAttemptRuntime</c> 之间重复的 try/finally 样板。
/// </summary>
public readonly struct AgentRunAmbientScope : IDisposable
{
    private readonly string? _previousBuildRunId;
    private readonly string? _previousRunId;
    private readonly string? _previousConversationId;
    private readonly ToolCapabilitySet? _previousCapabilities;
    private readonly ChannelWriter<object>? _previousSubagentWriter;
    private readonly RunSeal? _previousPlanSeal;

    private AgentRunAmbientScope(
        string? previousBuildRunId,
        string? previousRunId,
        string? previousConversationId,
        ToolCapabilitySet? previousCapabilities,
        ChannelWriter<object>? previousSubagentWriter,
        RunSeal? previousPlanSeal)
    {
        _previousBuildRunId = previousBuildRunId;
        _previousRunId = previousRunId;
        _previousConversationId = previousConversationId;
        _previousCapabilities = previousCapabilities;
        _previousSubagentWriter = previousSubagentWriter;
        _previousPlanSeal = previousPlanSeal;
    }

    /// <summary>快照当前全部 ambient 值（供外层 try/finally 先快照、后按分支 Apply）。</summary>
    public static AgentRunAmbientScope CaptureCurrent()
        => new(
            OneCodeAgentRunContext.CurrentBuildRunId,
            OneCodeAgentRunContext.CurrentRunId,
            ToolActivationContext.CurrentConversationId,
            ToolActivationContext.CurrentCapabilities,
            OneCodeAgentRunContext.SubagentEventWriter,
            OneCodeAgentRunContext.PlanSubmissionSeal);

    /// <summary>快照当前 ambient 值并立即写入新上下文，Dispose 时全量恢复。</summary>
    public static AgentRunAmbientScope Enter(
        string? buildRunId,
        string? runId,
        string? conversationId,
        ToolCapabilitySet? capabilities,
        ChannelWriter<object>? subagentWriter = null,
        RunSeal? planSubmissionSeal = null)
    {
        var scope = CaptureCurrent();
        Apply(buildRunId, runId, conversationId, capabilities, subagentWriter, planSubmissionSeal);
        return scope;
    }

    /// <summary>应用本轮运行期的 ambient 上下文。</summary>
    public static void Apply(
        string? buildRunId,
        string? runId,
        string? conversationId,
        ToolCapabilitySet? capabilities,
        ChannelWriter<object>? subagentWriter = null,
        RunSeal? planSubmissionSeal = null)
    {
        OneCodeAgentRunContext.CurrentBuildRunId = buildRunId;
        OneCodeAgentRunContext.CurrentRunId = runId;
        OneCodeAgentRunContext.PlanSubmissionSeal = planSubmissionSeal;
        ToolActivationContext.CurrentConversationId = conversationId;
        ToolActivationContext.CurrentCapabilities = capabilities;
        if (subagentWriter is not null)
            OneCodeAgentRunContext.SubagentEventWriter = subagentWriter;
    }

    public void Dispose()
    {
        OneCodeAgentRunContext.CurrentBuildRunId = _previousBuildRunId;
        OneCodeAgentRunContext.CurrentRunId = _previousRunId;
        ToolActivationContext.CurrentConversationId = _previousConversationId;
        ToolActivationContext.CurrentCapabilities = _previousCapabilities;
        OneCodeAgentRunContext.SubagentEventWriter = _previousSubagentWriter;
        OneCodeAgentRunContext.PlanSubmissionSeal = _previousPlanSeal;
    }
}

/// <summary>run 级一次性封条：落封后不可撤销，并行工具批次下线程安全。</summary>
public sealed class RunSeal
{
    private int _sealed;

    public bool IsSealed => Volatile.Read(ref _sealed) == 1;

    public void Seal() => Volatile.Write(ref _sealed, 1);
}
