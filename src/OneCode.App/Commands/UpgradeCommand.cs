using OneCode.Core.Product;
using OneCode.App.Services.Setup;

namespace OneCode.App.Commands;

/// <summary>
/// 检查并安装 OneCode 更新。
/// 无参数（默认 / --check）：只检查是否有新版本；--apply / -y / --yes：执行自动升级。
/// </summary>
public sealed class UpgradeCommand(
    ReleaseNotesService releaseNotesService,
    UpgradeService upgradeService) : Command
{
    public override string Name => "upgrade";
    public override string Description => "检查并安装 OneCode 更新";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[--apply|-y|--yes]";

    private static readonly ProductRepo Repo = ProductInfo.Default.Repository;

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var apply = Array.Exists(args, a => a is "--apply" or "-y" or "--yes");

        if (apply)
            return await PerformUpgradeAsync(ct).ConfigureAwait(false);

        // 默认 / --check:只检查版本,不执行升级
        return await CheckAndReportAsync(ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> CheckAndReportAsync(CancellationToken ct)
    {
        var check = await releaseNotesService.CheckLatestVersionAsync(ct).ConfigureAwait(false);

        if (check.LatestVersion is null)
            return CommandResult.Text(
                $"当前版本: v{check.CurrentVersion}\n"
                + "无法检查更新(网络错误)。\n"
                + $"手动检查: {Repo.ReleasesUrl}");

        if (!check.IsUpdateAvailable)
            return CommandResult.Text($"已是最新版本: v{check.CurrentVersion}");

        return CommandResult.Text(
            $"发现新版本: v{check.LatestVersion}\n"
            + $"当前版本: v{check.CurrentVersion}\n"
            + $"运行 /upgrade --apply 执行自动升级\n"
            + $"或手动下载: {check.DownloadUrl}");
    }

    private async Task<CommandResult> PerformUpgradeAsync(CancellationToken ct)
    {
        var result = await upgradeService.PerformUpgradeAsync(ct).ConfigureAwait(false);
        return result.Success
            ? CommandResult.Text(result.Message)
            : CommandResult.Error(result.Message);
    }
}
