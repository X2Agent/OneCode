namespace OneCode.Core.Domain;

/// <summary>
/// 文件变更记录（增量 Diff 结果）。
/// 由 EditTransactionMiddleware 在文件编辑后计算并发射，
/// 通过回调传递给上层（App/TUI 层负责转换为 TuiFileChange 或其他 UI 事件）。
///
/// 此类型位于 Core 层以解耦 Infrastructure 中间件对 App/Tui 类型的依赖。
/// </summary>
public sealed record FileChange(
    string FileName,
    IReadOnlyList<string> AddedLines,
    IReadOnlyList<string> RemovedLines);
