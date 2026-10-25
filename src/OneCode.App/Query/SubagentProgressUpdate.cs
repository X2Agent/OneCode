namespace OneCode.App.Query;

/// <summary>子代理 fork 的生命周期阶段。</summary>
public enum SubagentProgressPhase
{
    /// <summary>fork 已受理并开始执行。</summary>
    Started,
    /// <summary>执行中（工具调用计数推进）。</summary>
    Running,
    /// <summary>正常完成。</summary>
    Completed,
    /// <summary>失败/取消收尾。</summary>
    Failed,
}

/// <summary>
/// 子代理进度旁路信封——由 ForkedAgentRunner 写入父 run 的原始事件通道
/// （不经过 MAF AgentResponseUpdate），由 <c>StreamingSession.Digest</c> 投影为
/// <see cref="SubagentProgressEvent"/>，本身不是 UI 契约事件。
/// </summary>
public sealed record SubagentProgressUpdate(
    string ForkId,
    string AgentName,
    SubagentProgressPhase Phase,
    int ToolCallCount = 0,
    int TurnCount = 0,
    string? CurrentToolName = null,
    long ElapsedMs = 0);
