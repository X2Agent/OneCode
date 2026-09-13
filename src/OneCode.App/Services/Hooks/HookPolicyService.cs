
using OneCode.Core.Config;

using OneCode.Core.IO;
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

        foreach (var trustedDir in trusted)
        {
            if (PathBoundary.IsWithinDirectory(cwd, trustedDir, PathBoundary.DefaultComparison))
                return true;
        }
        return false;
    }
}
