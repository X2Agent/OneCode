using OneCode.App.Session;
using OneCode.Core.Config;
using OneCode.Core.Domain;
using OneCode.Core.Session;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.AutoDream;
using OneCode.App.Tools;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using System.Diagnostics;

namespace OneCode.Tests;

/// <summary>
/// Tests for AutoDream project-awareness: state file isolation and session scanning
/// filtered by the current working directory.
/// </summary>
/// <remarks>
/// 会话文件通过真实 <see cref="FileSessionEventStore"/> 写出（而非手搓 JSON），
/// 以保证测试覆盖「存储写入路径 ↔ 扫描读取路径」的目录与格式契约。
/// 历史教训：旧测试自建 <c>sessions/</c> 目录并手写 header，
/// 掩盖了「扫描目录与真实事件目录不一致」的 BUG（规则见
/// <c>src/OneCode.Tests/AGENTS.md</c>「契约测试：让写入路径与读取路径互相验证」）。
/// </remarks>
public sealed class AutoDreamProjectAwarenessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _userHome;
    private readonly string _globalConfigDir;
    private readonly string _eventsDir;
    private readonly string _projectA;
    private readonly string _projectB;
    private readonly IWorkingDirectoryAccessor _wdAccessor;
    private readonly AutoDreamService _service;
    private readonly FileSessionEventStore _eventStore;

    public AutoDreamProjectAwarenessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AutoDream_{Guid.NewGuid():N}");
        // _userHome 模拟用户主目录（~），_globalConfigDir 模拟配置文件目录（~/.onecode）。
        // 生产环境：FileSessionEventStore 接收 ~，内部拼 .onecode/events；
        // AutoDreamService 接收 ~/.onecode，扫描器拼 events。两者殊途同归。
        _userHome = Path.Combine(_tempDir, "home");
        _globalConfigDir = Path.Combine(_userHome, ".onecode");
        _eventsDir = Path.Combine(_globalConfigDir, "events");
        _projectA = Path.Combine(_tempDir, "projectA");
        _projectB = Path.Combine(_tempDir, "projectB");
        Directory.CreateDirectory(_projectA);
        Directory.CreateDirectory(_projectB);

        _wdAccessor = Substitute.For<IWorkingDirectoryAccessor>();
        _wdAccessor.WorkingDirectory.Returns(_projectA);

        // 真实事件存储：basePath = 用户主目录（生产为 PathsHelper.UserHome）。
        _eventStore = new FileSessionEventStore(_userHome);

        _service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(Substitute.For<IChatClient>(), new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null), Substitute.For<IModelManager>(), new PromptManager()),
            storage: new AutoDreamStorageDependencies(Substitute.For<IMemoryEntryStore>(), Substitute.For<IConfigManager>(), _wdAccessor),
            globalConfigDirOverride: _globalConfigDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"Cleanup failed: {ex.Message}"); }
    }

    // State file location

    [Fact]
    public void GetProjectStateDir_ReturnsProjectMemorySubdir()
    {
        var stateDir = _service.GetProjectStateDir();

        stateDir.Should().Be(Path.Combine(_projectA, ".onecode", "memory"));
    }

    [Fact]
    public void GetStateFilePath_StoredInProjectDirectory()
    {
        var lockPath = _service.GetStateFilePath("autodream.lock");

        lockPath.Should().Be(Path.Combine(_projectA, ".onecode", "memory", "autodream.lock"));
        lockPath.Should().NotStartWith(_globalConfigDir, "state files must NOT be in the global config dir");
    }

    [Fact]
    public void GetStateFilePath_FallsBackToGlobal_WhenWorkingDirIsEmpty()
    {
        _wdAccessor.WorkingDirectory.Returns("");

        var statePath = _service.GetStateFilePath("last_consolidated_at");

        statePath.Should().StartWith(_globalConfigDir, "empty working dir should fall back to global");
    }

    [Fact]
    public void SetLastConsolidatedAt_WritesToProjectDirectory()
    {
        var time = DateTimeOffset.UtcNow;

        _service.SetLastConsolidatedAt(time);

        var stateFile = Path.Combine(_projectA, ".onecode", "memory", "last_consolidated_at");
        File.Exists(stateFile).Should().BeTrue("state file should be written to project directory");
        var readBack = _service.GetLastConsolidatedAt();
        readBack.Should().BeCloseTo(time, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void StateFiles_AreIsolatedBetweenProjects()
    {
        // Project A sets a consolidation time
        _wdAccessor.WorkingDirectory.Returns(_projectA);
        var timeA = DateTimeOffset.UtcNow;
        _service.SetLastConsolidatedAt(timeA);

        // Project B has its own independent state
        _wdAccessor.WorkingDirectory.Returns(_projectB);
        var timeB = timeA.AddHours(12);
        _service.SetLastConsolidatedAt(timeB);

        // Project A's state should be unchanged
        _wdAccessor.WorkingDirectory.Returns(_projectA);
        var readA = _service.GetLastConsolidatedAt();
        readA.Should().BeCloseTo(timeA, TimeSpan.FromSeconds(1), "projectA state should be isolated from projectB");

        // Project B's state should be its own
        _wdAccessor.WorkingDirectory.Returns(_projectB);
        var readB = _service.GetLastConsolidatedAt();
        readB.Should().BeCloseTo(timeB, TimeSpan.FromSeconds(1), "projectB should have its own state");
    }

    // Session scanning

    [Fact]
    public async Task CountNewSessionsSince_FiltersByProjectWorkingDirectory()
    {
        var since = DateTimeOffset.UtcNow.AddHours(-1);

        // Create 3 sessions for projectA, 2 for projectB
        await CreateSessionFileAsync(Guid.NewGuid(), _projectA);
        await CreateSessionFileAsync(Guid.NewGuid(), _projectA);
        await CreateSessionFileAsync(Guid.NewGuid(), _projectA);
        await CreateSessionFileAsync(Guid.NewGuid(), _projectB);
        await CreateSessionFileAsync(Guid.NewGuid(), _projectB);

        _wdAccessor.WorkingDirectory.Returns(_projectA);
        var countA = _service.CountNewSessionsSince(since);
        countA.Should().Be(3, "only projectA sessions should be counted");

        _wdAccessor.WorkingDirectory.Returns(_projectB);
        var countB = _service.CountNewSessionsSince(since);
        countB.Should().Be(2, "only projectB sessions should be counted");
    }

    [Fact]
    public async Task CountNewSessionsSince_UsesJsonlExtension_NotJson()
    {
        var since = DateTimeOffset.UtcNow.AddHours(-1);

        // Create a .jsonl session file (correct extension)
        await CreateSessionFileAsync(Guid.NewGuid(), _projectA);

        // Also create a .json file (wrong extension, should be ignored)
        var jsonFile = Path.Combine(_eventsDir, $"{Guid.NewGuid()}.json");
        await File.WriteAllTextAsync(jsonFile, "{}");

        _wdAccessor.WorkingDirectory.Returns(_projectA);
        var count = _service.CountNewSessionsSince(since);

        count.Should().Be(1, "only .jsonl files should be matched, not .json");
    }

    [Fact]
    public async Task CountNewSessionsSince_Returns0_WhenWorkingDirIsNull()
    {
        _wdAccessor.WorkingDirectory.Returns((string)null!);

        var count = _service.CountNewSessionsSince(DateTimeOffset.MinValue);

        count.Should().Be(0, "should return 0 when workingDirectory is not available");
    }

    [Fact]
    public async Task CountNewSessionsSince_OnlyCountsSessionsAfterSince()
    {
        var oldTime = DateTimeOffset.UtcNow.AddHours(-2);
        var since = DateTimeOffset.UtcNow.AddHours(-1);

        // Create an "old" session (before `since`)
        var oldFile = await CreateSessionFileAsync(Guid.NewGuid(), _projectA);
        File.SetLastWriteTimeUtc(oldFile, oldTime.UtcDateTime);

        // Create a "new" session (after `since`)
        await CreateSessionFileAsync(Guid.NewGuid(), _projectA);

        _wdAccessor.WorkingDirectory.Returns(_projectA);
        var count = _service.CountNewSessionsSince(since);

        count.Should().Be(1, "only sessions modified after `since` should be counted");
    }

    [Fact]
    public async Task IsSessionForProject_MatchesWorkingDirectory()
    {
        var sessionId = Guid.NewGuid();
        var file = await CreateSessionFileAsync(sessionId, _projectA);

        var result = _service.IsSessionForProject(file, _projectA);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsSessionForProject_RejectsNonMatchingProject()
    {
        var sessionId = Guid.NewGuid();
        var file = await CreateSessionFileAsync(sessionId, _projectA);

        var result = _service.IsSessionForProject(file, _projectB);

        result.Should().BeFalse("session for projectA should not match projectB");
    }

    [Fact]
    public async Task IsSessionForProject_HandlesTrailingSlashDifference()
    {
        var sessionId = Guid.NewGuid();
        var file = await CreateSessionFileAsync(sessionId, _projectA);

        // projectA with trailing separator
        var result = _service.IsSessionForProject(file, _projectA + Path.DirectorySeparatorChar);

        result.Should().BeTrue("trailing slash should be normalized for comparison");
    }

    // Helpers

    /// <summary>
    /// 通过真实 <see cref="FileSessionEventStore"/> 写入一个会话的 <c>SessionStarted</c> 事件，
    /// 返回落盘的事件文件路径。
    /// </summary>
    /// <remarks>
    /// 使用生产写入路径而非手写 JSON，使测试同时约束「目录名」与「事件信封格式
    /// （<c>payload.working_directory</c>）」两项契约。
    /// </remarks>
    private async Task<string> CreateSessionFileAsync(Guid sessionId, string workingDirectory)
    {
        var conversation = new Conversation
        {
            Id = new SessionId(sessionId.ToString()),
            Name = $"test-{sessionId:N}",
            WorkingDirectory = workingDirectory,
            Model = "test-model",
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };

        var events = ConversationEventMapper.ToEvents(conversation);
        await _eventStore.AppendAsync(events);

        return Path.Combine(_eventsDir, $"{sessionId}.jsonl");
    }
}
