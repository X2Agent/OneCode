using System.Text.Json;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Services;
using OneCode.App.Session;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// /add-dir 回归测试（B5）：Effective.AllowedDirectories 的 getter 每次返回新副本，
/// 历史实现直接 mutate 副本导致会话级与 --persist 两条路径都写丢。
/// </summary>
public sealed class AddDirCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _userDir;
    private readonly string _projectDir;
    private readonly string _targetDir;

    public AddDirCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"AddDirCommandTests_{Guid.NewGuid():N}");
        _userDir = Path.Combine(_root, "user");
        _projectDir = Path.Combine(_root, "project");
        _targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(_userDir);
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_targetDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ProjectSettingsPath => Path.Combine(_projectDir, "settings.json");

    private AddDirCommand CreateSut(ConfigManager configManager) => new(configManager);

    [Fact]
    public async Task List_NoDirectories_ShowsEmptyMessage()
    {
        using var configManager = new ConfigManager(_userDir, _projectDir);
        var sut = CreateSut(configManager);

        var result = await sut.ExecuteAsync([], TestContext.Current.CancellationToken);

        var text = AssertTextResult(result);
        text.Should().Contain("No additional directories added.");
    }

    [Fact]
    public async Task Add_SessionScope_BecomesVisibleInAdditionalDirectories()
    {
        using var configManager = new ConfigManager(_userDir, _projectDir);
        var sut = CreateSut(configManager);

        var result = await sut.ExecuteAsync([_targetDir], TestContext.Current.CancellationToken);

        AssertTextResult(result).Should().Contain("session only");
        configManager.Current.Effective.AllowedDirectories
            .Should().Contain(_targetDir, "session-level add must reach Effective.AllowedDirectories");

        // 消费方视角：SessionWorkingDirectoryAccessor.AdditionalDirectories 读取同一 Effective 快照。
        var sessionManager = Substitute.For<ISessionWorkingDirectory>();
        var accessor = new SessionWorkingDirectoryAccessor(sessionManager, configManager);
        accessor.AdditionalDirectories.Should().Contain(_targetDir);

        // 会话级不落盘：项目/用户配置文件不应出现 allowedDirectories。
        File.Exists(ProjectSettingsPath).Should().BeFalse();
        var userSettingsPath = Path.Combine(_userDir, "settings.json");
        if (File.Exists(userSettingsPath))
            File.ReadAllText(userSettingsPath).Should().NotContain("allowedDirectories");
    }

    [Fact]
    public async Task Add_Persist_WritesPathIntoProjectSettings()
    {
        using var configManager = new ConfigManager(_userDir, _projectDir);
        var sut = CreateSut(configManager);

        var result = await sut.ExecuteAsync([_targetDir, "--persist"], TestContext.Current.CancellationToken);

        AssertTextResult(result).Should().Contain("persisted to project config");
        File.Exists(ProjectSettingsPath).Should().BeTrue();
        using var document = JsonDocument.Parse(File.ReadAllText(ProjectSettingsPath));
        document.RootElement.TryGetProperty("allowedDirectories", out var dirs).Should().BeTrue();
        dirs.ValueKind.Should().Be(JsonValueKind.Array);
        dirs.EnumerateArray().Select(d => d.GetString())
            .Should().Contain(_targetDir);
        configManager.Current.Effective.AllowedDirectories.Should().Contain(_targetDir);
    }

    [Fact]
    public async Task Add_SameDirectoryTwice_DoesNotDuplicate()
    {
        using var configManager = new ConfigManager(_userDir, _projectDir);
        var sut = CreateSut(configManager);

        await sut.ExecuteAsync([_targetDir], TestContext.Current.CancellationToken);
        await sut.ExecuteAsync([_targetDir], TestContext.Current.CancellationToken);

        configManager.Current.Effective.AllowedDirectories
            .Should().ContainSingle(d => d.Equals(_targetDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Add_NonexistentDirectory_ReturnsError()
    {
        using var configManager = new ConfigManager(_userDir, _projectDir);
        var sut = CreateSut(configManager);
        var missing = Path.Combine(_root, "missing");

        var result = await sut.ExecuteAsync([missing], TestContext.Current.CancellationToken);

        AssertErrorResult(result).Should().Contain("Directory not found");
    }

    private static string AssertTextResult(OneCode.Core.Commands.CommandResult result)
    {
        result.Should().BeOfType<OneCode.Core.Commands.CommandResult.TextResult>();
        return ((OneCode.Core.Commands.CommandResult.TextResult)result).Value;
    }

    private static string AssertErrorResult(OneCode.Core.Commands.CommandResult result)
    {
        result.Should().BeOfType<OneCode.Core.Commands.CommandResult.ErrorResult>();
        return ((OneCode.Core.Commands.CommandResult.ErrorResult)result).Message;
    }
}
