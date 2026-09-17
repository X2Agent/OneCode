namespace OneCode.Core.Exec;

/// <summary>Shell 执行结果，保留输出、退出码和截断状态。</summary>
public sealed record ShellExecutionResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool Truncated);