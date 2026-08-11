using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 策略控制——工作区信任检查。
/// </summary>
public sealed class HookPolicyService
{
    private readonly IConfigManager _configManager;

    public HookPolicyService(IConfigManager configManager)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
    }

    public bool IsCurrentWorkspaceTrusted()
    {
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        var trusted = _configManager.Current.Effective.TrustedDirectories;

        var normalizedCwd = PathsHelper.NormalizePath(cwd);
        foreach (var trustedDir in trusted)
        {
            var normalizedTrusted = PathsHelper.NormalizePath(trustedDir);
            if (normalizedCwd.Equals(normalizedTrusted, PathComparison)
                || normalizedCwd.StartsWith(normalizedTrusted + Path.DirectorySeparatorChar, PathComparison))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
