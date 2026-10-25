using OneCode.App.Query;

namespace OneCode.App.Services.Streaming;

/// <summary>
/// 宿主（TUI / WebUI）消费的查询流端口：普通查询、命令 prompt、循环任务与工作流恢复
/// 共用同一条 <see cref="QueryEvent"/> 管道。抽接口是为了宿主可替换（测试替身 / 未来前端）。
/// </summary>
public interface IQueryStreamService
{
    /// <summary>按 WorkingMode 派发普通查询（Build/Plan 走 Normal 流，Goal 走编排流）。</summary>
    IAsyncEnumerable<QueryEvent> StreamQueryAsync(
        InteractiveSession session,
        string text,
        IReadOnlyList<string>? imagePaths,
        CancellationToken ct = default);

    /// <summary>把命令产出的 prompt 经普通查询流水线流出（与普通查询同一 UX）。</summary>
    IAsyncEnumerable<QueryEvent> StreamCommandPromptAsync(
        InteractiveSession session,
        string prompt,
        string[]? allowedTools,
        CancellationToken ct = default);

    /// <summary>运行 <c>/loop</c> 的有界确定性循环。</summary>
    IAsyncEnumerable<QueryEvent> StreamLoopAsync(
        InteractiveSession session,
        string task,
        string? checkCommand,
        int maxIterations,
        CancellationToken ct = default);

    /// <summary>从检查点恢复持久化工作流。</summary>
    IAsyncEnumerable<QueryEvent> StreamResumeWorkflowAsync(
        InteractiveSession session,
        string sessionId,
        WorkflowResumeKind kind,
        CancellationToken ct = default);
}
