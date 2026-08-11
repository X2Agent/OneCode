namespace OneCode.Infrastructure;

public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}
