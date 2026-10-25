using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Setup;
using OneCode.Core.IO;

namespace OneCode.Tests;

public sealed class UpgradeServiceTests
{
    [Fact]
    public async Task ReplaceInstallAsync_RenamesExecutableInPlaceAndDoesNotLeaveLockedExeInTempDir()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"onecode-upgrade-test-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(rootDir, "source");
        var installDir = Path.Combine(rootDir, "install");
        var tempDir = Path.Combine(rootDir, "temp");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            var exeName = OperatingSystem.IsWindows() ? "OneCode.Tui.exe" : "OneCode.Tui";
            var currentExePath = Path.Combine(installDir, "onecode.exe");
            await File.WriteAllTextAsync(currentExePath, "old-binary");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, exeName), "new-binary");
            Directory.CreateDirectory(Path.Combine(sourceDir, "prompts"));
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "prompts", "system.md"), "new-prompt");

            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            var releaseNotes = new ReleaseNotesService(httpClientFactory, NullLogger<ReleaseNotesService>.Instance);
            var processRunner = Substitute.For<IProcessRunner>();
            processRunner.ExecuteWithArgumentListAsync(default!, default!)
                .ReturnsForAnyArgs(new ProcessResult(0, string.Empty, string.Empty));

            var service = new UpgradeService(
                httpClientFactory,
                releaseNotes,
                processRunner,
                NullLogger<UpgradeService>.Instance);

            await service.ReplaceInstallAsync(
                sourceDir,
                installDir,
                currentExePath,
                exeName,
                tempDir,
                CancellationToken.None);

            (await File.ReadAllTextAsync(currentExePath)).Should().Be("new-binary");
            (await File.ReadAllTextAsync(Path.Combine(installDir, "prompts", "system.md"))).Should().Be("new-prompt");
            File.Exists(Path.Combine(tempDir, "install-backup", exeName)).Should().BeFalse(
                "旧可执行文件应在安装目录内原地重命名而非移入临时目录");
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, recursive: true);
        }
    }

    [Fact]
    public void TryCleanupStaleOldExecutable_DeletesExistingOldFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-old-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var exePath = Path.Combine(tempDir, "onecode.exe");
            var oldPath = exePath + ".old";
            File.WriteAllText(oldPath, "stale-old-binary");

            UpgradeService.TryCleanupStaleOldExecutable(exePath);

            File.Exists(oldPath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
