using System.Threading.Channels;

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
}

/// <summary>run 级一次性封条：落封后不可撤销，并行工具批次下线程安全。</summary>
public sealed class RunSeal
{
    private int _sealed;

    public bool IsSealed => Volatile.Read(ref _sealed) == 1;

    public void Seal() => Volatile.Write(ref _sealed, 1);
}
