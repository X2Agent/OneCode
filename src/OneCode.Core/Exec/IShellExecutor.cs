namespace OneCode.Core.Exec;

/// <summary>可替换的 Shell 执行能力。</summary>
public interface IShellExecutor
{
    /// <summary>执行一次 Shell 请求。</summary>
    Task<ShellExecutionResult> ExecuteAsync(
        ShellExecutionRequest request,
        CancellationToken ct = default);
}