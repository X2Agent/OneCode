using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OneCode.Core.Product;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Setup;

/// <summary>
/// 执行 OneCode 自更新:下载 GitHub Release 资产 → 校验 SHA256 → 解压 → 原地替换当前安装。
/// 升级完成后需重启进程才能使用新版本(当前进程仍运行旧代码)。
/// </summary>
public sealed class UpgradeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ReleaseNotesService _releaseNotesService;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<UpgradeService> _logger;

    // 下载大文件(自包含单文件可达 80MB+)需要比 API 调用更长的超时
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public UpgradeService(
        IHttpClientFactory httpClientFactory,
        ReleaseNotesService releaseNotesService,
        IProcessRunner processRunner,
        ILogger<UpgradeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _releaseNotesService = releaseNotesService;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>
    /// 执行完整升级流程:检查版本 → 下载 → 校验 → 解压 → 替换。
    /// </summary>
    public async Task<UpgradeResult> PerformUpgradeAsync(CancellationToken ct = default)
    {
        // 1. 检查版本
        var check = await _releaseNotesService.CheckLatestVersionAsync(ct).ConfigureAwait(false);

        if (check.LatestVersion is null)
            return UpgradeResult.Failed(
                "无法获取最新版本信息(网络错误)。请稍后重试或手动下载: "
                + ProductInfo.Default.Repository.ReleasesUrl);

        if (!check.IsUpdateAvailable)
            return new UpgradeResult(false, "已是最新版本,无需升级。", check.CurrentVersion, null);

        var targetVersion = check.LatestVersion;
        var currentVersion = check.CurrentVersion ?? "unknown";

        // 2. 确保当前是正式安装(非 dotnet run 开发环境)
        var currentExePath = Environment.ProcessPath;
        if (currentExePath is null)
            return UpgradeResult.Failed("无法确定当前可执行文件路径(Environment.ProcessPath 为 null)。");

        var currentExeName = Path.GetFileName(currentExePath);
        if (!currentExeName.Contains("OneCode.Cli", StringComparison.OrdinalIgnoreCase) &&
            !currentExeName.Contains("onecode", StringComparison.OrdinalIgnoreCase))
        {
            return UpgradeResult.Failed(
                $"当前进程 '{currentExeName}' 不是正式安装的 OneCode,无法自动升级。\n"
                + "请通过安装脚本安装后再使用 /upgrade --apply。");
        }

        // 3. 确定资产 URL
        var rid = GetRuntimeIdentifier();
        var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        var assetName = $"onecode-{targetVersion}-{rid}.{ext}";
        var repo = ProductInfo.Default.Repository;
        var downloadUrl = $"{repo.Url}/releases/download/v{targetVersion}/{assetName}";
        var checksumUrl = $"{downloadUrl}.sha256";

        // 4. 准备临时目录
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 5. 下载资产 + checksum
            var archivePath = Path.Combine(tempDir, assetName);
            var checksumPath = Path.Combine(tempDir, $"{assetName}.sha256");

            await DownloadFileAsync(downloadUrl, archivePath, ct).ConfigureAwait(false);
            await DownloadFileAsync(checksumUrl, checksumPath, ct).ConfigureAwait(false);

            // 6. 校验 SHA256
            if (!await VerifySha256Async(archivePath, checksumPath, ct).ConfigureAwait(false))
            {
                return UpgradeResult.Failed(
                    "SHA256 校验失败,下载文件可能损坏或被篡改。\n"
                    + $"请重试 /upgrade --apply,或手动下载: {downloadUrl}");
            }

            // 7. 解压
            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            if (OperatingSystem.IsWindows())
            {
                ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
            }
            else
            {
                ExtractTarGz(archivePath, extractDir);
            }

            // 8. 验证解压内容(发布包根目录必须包含主程序)
            var sourceExeName = OperatingSystem.IsWindows() ? "OneCode.Cli.exe" : "OneCode.Cli";
            var sourceExePath = Path.Combine(extractDir, sourceExeName);
            if (!File.Exists(sourceExePath))
            {
                return UpgradeResult.Failed(
                    $"归档中找不到 {sourceExeName},发布包可能不完整。\n"
                    + $"请手动下载: {downloadUrl}");
            }

            // 9. 原地替换当前安装
            var installDir = Path.GetDirectoryName(currentExePath);
            if (string.IsNullOrEmpty(installDir))
                return UpgradeResult.Failed("无法确定安装目录。");

            await ReplaceInstallAsync(
                extractDir,
                installDir,
                currentExePath,
                sourceExeName,
                tempDir,
                ct).ConfigureAwait(false);

            return new UpgradeResult(
                true,
                $"升级成功: v{currentVersion} → v{targetVersion}\n"
                + "请重启 OneCode 以使用新版本。",
                currentVersion,
                targetVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "升级到 v{Version} 失败", targetVersion);
            return UpgradeResult.Failed(
                $"升级过程中出错: {ex.Message}\n"
                + "如安装已损坏,请重新运行安装脚本修复: "
                + ProductInfo.Default.Repository.Url);
        }
        finally
        {
            // 清理临时目录(失败则忽略,OS 会在重启后清理 Temp)
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up upgrade temporary directory {Path}", tempDir);
            }
        }
    }

    private static string GetRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"不支持的架构: {RuntimeInformation.OSArchitecture}")
        };
        return $"{os}-{arch}";
    }

    private async Task DownloadFileAsync(string url, string targetPath, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(Constants.HttpClientNames.Upgrade);
        http.Timeout = DownloadTimeout;

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(targetPath);
        await content.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    private static async Task<bool> VerifySha256Async(
        string archivePath,
        string checksumPath,
        CancellationToken ct)
    {
        var checksumText = (await File.ReadAllTextAsync(checksumPath, ct).ConfigureAwait(false)).Trim();
        // .sha256 文件格式: "{hash}  {filename}"(见 release.yml 的 Set-Content 输出)
        var expectedHash = checksumText.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];

        await using var archive = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(archive, ct).ConfigureAwait(false));

        return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractTarGz(string archivePath, string extractDir)
    {
        using var fs = File.OpenRead(archivePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gz, extractDir, overwriteFiles: true);
    }

    /// <summary>
    /// 原地替换当前安装目录中的文件。
    /// Windows: 正在运行的 exe 不能直接覆盖,但可以重命名。
    ///   策略: 重命名当前 exe → .old → 复制新 exe → 复制资源 → 尝试删除 .old。
    /// Unix: 可以直接覆盖正在运行的 exe(进程持有 inode,文件名替换不影响)。
    /// </summary>
    private async Task ReplaceInstallAsync(
        string sourceDir,
        string installDir,
        string currentExePath,
        string sourceExeName,
        string tempDir,
        CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var sourceExePath = Path.Combine(sourceDir, sourceExeName);
        var backupDir = Path.Combine(tempDir, "install-backup");
        Directory.CreateDirectory(backupDir);
        var backedUpFiles = new List<(string Target, string Backup)>();
        var createdFiles = new List<string>();
        var backupExePath = Path.Combine(backupDir, sourceExeName);

        try
        {
            // Rename the old executable first. This keeps the running Unix inode intact and
            // avoids truncating the only launchable copy when the new copy runs out of space.
            File.Move(currentExePath, backupExePath);
            backedUpFiles.Add((currentExePath, backupExePath));

            File.Copy(sourceExePath, currentExePath);
            createdFiles.Add(currentExePath);

            // File.Copy does not preserve Unix executable permissions.
            if (!isWindows)
            {
                var chmod = await _processRunner.ExecuteWithArgumentListAsync(
                    "chmod",
                    ["+x", currentExePath],
                    ct: ct).ConfigureAwait(false);
                if (chmod is not { Success: true })
                    throw new IOException($"chmod failed for '{currentExePath}'.");
            }

            // Back up every existing target before copying resources so a partial resource
            // update can be rolled back without deleting user files outside the release.
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, sourceFile);
                if (relative == sourceExeName)
                    continue;

                var target = Path.Combine(installDir, relative);
                var backup = Path.Combine(backupDir, relative);
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var targetExists = File.Exists(target);
                if (targetExists)
                {
                    var backupParent = Path.GetDirectoryName(backup);
                    if (!string.IsNullOrEmpty(backupParent))
                        Directory.CreateDirectory(backupParent);
                    File.Copy(target, backup);
                    backedUpFiles.Add((target, backup));
                }
                else
                {
                    // Register before copying so a partially created file is removed on failure.
                    createdFiles.Add(target);
                }

                File.Copy(sourceFile, target, overwrite: true);
            }
        }
        catch
        {
            try
            {
                foreach (var createdFile in createdFiles)
                {
                    if (File.Exists(createdFile))
                        File.Delete(createdFile);
                }

                foreach (var (target, backup) in backedUpFiles.AsEnumerable().Reverse())
                {
                    if (File.Exists(backup))
                        File.Copy(backup, target, overwrite: true);
                }
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(
                    rollbackEx,
                    "Upgrade rollback failed for installation directory {InstallDir}",
                    installDir);
                throw new InvalidOperationException(
                    "升级失败且回滚也失败!当前安装可能已损坏。\n"
                    + $"请重新运行安装脚本修复: {ProductInfo.Default.Repository.Url}",
                    rollbackEx);
            }

            throw;
        }
    }
}

/// <summary>
/// 升级操作结果。
/// </summary>
public sealed record UpgradeResult(
    bool Success,
    string Message,
    string? CurrentVersion,
    string? NewVersion)
{
    public static UpgradeResult Failed(string message) => new(false, message, null, null);
}
