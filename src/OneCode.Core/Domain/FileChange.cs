namespace OneCode.Core.Domain;

/// <summary>
/// 文件变更记录（增量 Diff 结果）。
/// 由 EditTransactionMiddleware 在文件编辑后计算并发射，
/// 通过回调传递给上层，由引擎投影为 <c>FileChangeEvent</c> 事件。
///
/// 此类型位于 Core 层以解耦 Infrastructure 中间件对上层类型的依赖。
/// Contributors 为多代理工作流中修改过该文件的代理 ID 列表（去重、按首次修改排序）；
/// 主 agent 路径为 null。
/// </summary>
public sealed record FileChange(
    string FileName,
    IReadOnlyList<string> AddedLines,
    IReadOnlyList<string> RemovedLines,
    IReadOnlyList<string>? Contributors = null);
