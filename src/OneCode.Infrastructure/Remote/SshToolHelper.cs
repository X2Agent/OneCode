namespace OneCode.Infrastructure.Remote;

/// <summary>
/// SSH helpers shared by BashTool / PowerShellTool.
/// </summary>
public static class SshToolHelper
{
    /// <summary>Whether an SSH connection is active.</summary>
    public static bool IsActive(SshRemoteService? ssh) => ssh is { IsConnected: true };
}
