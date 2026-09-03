namespace OneCode.Core.Workflows;

/// <summary>
/// 命令幂等（Plan 的 <c>LastProcessedCommandId</c> 资产外推为内核可选能力）：
/// load-modify-save 循环中，命令重复提交（重试/恢复重放/双通道竞态）时返回既有状态
/// 而非重复执行转换。语义：命令身份 = 聚合承载的 <c>LastProcessedCommandId</c>。
/// </summary>
public static class CommandIdempotency
{
    /// <summary>
    /// 命令是否为重放（幂等命中）：与聚合最近一次成功处理的命令标识相同即视为重放，
    /// 调用方应短路返回既有状态；聚合缺失（尚无持久化状态）一律视为新命令。
    /// </summary>
    /// <param name="aggregate">承载幂等命令标识的聚合（可为 null = 无既有状态）。</param>
    /// <param name="commandId">本次命令标识。</param>
    public static bool IsReplay(ICommandIdempotent? aggregate, string commandId)
        => aggregate?.LastProcessedCommandId == commandId;
}

/// <summary>承载幂等命令标识的聚合能力（可选内核能力，Plan 工作流聚合已实现）。</summary>
public interface ICommandIdempotent
{
    /// <summary>最近一次成功处理的命令标识；null 表示尚未处理任何命令。</summary>
    string? LastProcessedCommandId { get; }
}