using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.AutoDream;
using OneCode.App.Tools;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Config;
using System.Diagnostics;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>
/// Tests for AutoDream project-awareness: state file isolation and session scanning
/// filtered by the current working directory.
/// </summary>
public sealed class AutoDreamProjectAwarenessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _globalConfigDir;
    private readonly string _sessionsDir;
    private readonly string _projectA;
    private readonly string _projectB;
    private readonly IWorkingDirectoryAccessor _wdAccessor;
    private readonly AutoDreamService _service;

    public AutoDreamProjectAwarenessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AutoDream_{Guid.NewGuid():N}");
        _globalConfigDir = Path.Combine(_tempDir, "global");
        _sessionsDir = Path.Combine(_globalConfigDir, "sessions");
        _projectA = Path.Combine(_tempDir, "projectA");
        _projectB = Path.Combine(_tempDir, "projectB");
        Directory.CreateDirectory(_sessionsDir);
        Directory.CreateDirectory(_projectA);
        Directory.CreateDirectory(_projectB);

        _wdAccessor = Substitute.For<IWorkingDirectoryAccessor>();
        _wdAccessor.WorkingDirectory.Returns(_projectA);

        _service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(Substitute.For<IChatClient>(), new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null), Substitute.For<IModelManager>(), new PromptManager()),
            storage: new AutoDreamStorageDependencies(new InMemoryMemoryEntryStore(), Substitute.For<IConfigManager>(), _wdAccessor),
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
        var jsonFile = Path.Combine(_sessionsDir, $"{Guid.NewGuid()}.json");
        await File.WriteAllTextAsync(jsonFile, "{\"working_directory\":\"" + _projectA + "\"}");

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
    /// Creates a session .jsonl file with a header containing the given working directory,
    /// matching the SessionStore format (snake_case JSON header on the first line).
    /// </summary>
    private async Task<string> CreateSessionFileAsync(Guid sessionId, string workingDirectory)
    {
        var file = Path.Combine(_sessionsDir, $"{sessionId}.jsonl");
        var header = new
        {
            id = sessionId,
            name = $"test-{sessionId:N}",
            working_directory = workingDirectory,
            model = "test-model",
            status = "active",
            total_usage = new { input_tokens = 0, output_tokens = 0 },
            created_at = DateTimeOffset.UtcNow,
            last_activity_at = DateTimeOffset.UtcNow,
            branch = (string?)null,
            message_count = 1,
            metadata = (Dictionary<string, object>?)null,
            type = "session_header",
        };
        var headerJson = JsonSerializer.Serialize(header);
        await File.WriteAllTextAsync(file, headerJson + "\n");
        return file;
    }
}
