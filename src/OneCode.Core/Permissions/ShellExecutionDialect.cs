namespace OneCode.Core.Permissions;

/// <summary>Shell 请求方言与实际持久执行器语法的统一判定。</summary>
public static class ShellExecutionDialect
{
    /// <summary>
    /// Windows 持久 Shell 始终使用 PowerShell；其他平台仅在显式请求时使用 PowerShell。
    /// 权限、路径校验、告警和执行必须共用此结果，禁止各自按请求字段猜测。
    /// </summary>
    public static bool UsesPowerShell(string? requestedShell)
        => OperatingSystem.IsWindows()
            || string.Equals(requestedShell, "powershell", StringComparison.OrdinalIgnoreCase);
}
