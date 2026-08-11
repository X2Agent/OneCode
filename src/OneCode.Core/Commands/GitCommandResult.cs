namespace OneCode.Core.Commands;

/// <summary>
/// Git 命令执行结果的简化视图。仅包含调用方需要的字段，
/// 解除 Core 层对 Infrastructure 层 <c>ProcessResult</c> 的依赖。
/// </summary>
public sealed record GitCommandResult(bool Success, string Stdout, string Stderr);
