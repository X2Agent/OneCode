using System.Text.Json;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

public sealed class ConfigManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _userDir;
    private readonly string _projectDir;
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.OrdinalIgnoreCase);

    public ConfigManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ConfigManagerTests_{Guid.NewGuid():N}");
        _userDir = Path.Combine(_root, "user");
        _projectDir = Path.Combine(_root, "project");
        Directory.CreateDirectory(_userDir);
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string UserSettingsPath => Path.Combine(_userDir, "settings.json");
    private string ProjectSettingsPath => Path.Combine(_projectDir, "settings.json");

    [Fact]
    public void Resolve_UsesEnvironmentProjectUserBuiltInPriority()
    {
        File.WriteAllText(UserSettingsPath, """{"model":"user-model","maxTurns":10}""");
        File.WriteAllText(ProjectSettingsPath, """{"model":"project-model","maxTurns":20}""");
        SetEnvironment("ONECODE_MODEL", "environment-model");

        using var sut = new ConfigManager(_userDir, _projectDir);

        sut.Current.Effective.Model.Should().Be("environment-model");
        sut.Current.GetValueInfo("model").Source.Should().Be(ConfigScope.Environment);
        sut.Current.Effective.MaxTurns.Should().Be(20);
        sut.Current.GetValueInfo("maxTurns").Source.Should().Be(ConfigScope.Project);
        sut.Current.Effective.NotificationsEnabled.Should().BeFalse();
        sut.Current.GetValueInfo("notificationsEnabled").Source.Should().Be(ConfigScope.BuiltIn);
    }

    [Fact]
    public async Task ApplyUserPatch_DoesNotCopyProjectOrEnvironmentValuesIntoUserFile()
    {
        File.WriteAllText(ProjectSettingsPath, """{"model":"project-model"}""");
        SetEnvironment("ONECODE_API_KEY", "environment-secret");
        using var sut = new ConfigManager(_userDir, _projectDir);

        var result = await sut.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "maxTurns", 42),
            TestContext.Current.CancellationToken);

        result.Saved.Should().BeTrue();
        using var document = JsonDocument.Parse(File.ReadAllText(UserSettingsPath));
        document.RootElement.TryGetProperty("maxTurns", out var maxTurns).Should().BeTrue();
        maxTurns.GetInt32().Should().Be(42);
        document.RootElement.TryGetProperty("model", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("apiKey", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveProjectValue_FallsBackToUserValue()
    {
        File.WriteAllText(UserSettingsPath, """{"model":"user-model"}""");
        File.WriteAllText(ProjectSettingsPath, """{"model":"project-model"}""");
        using var sut = new ConfigManager(_userDir, _projectDir);

        var result = await sut.ApplyAsync(
            new ConfigPatch(ConfigScope.Project, new Dictionary<string, ConfigMutation>
            {
                ["model"] = new ConfigMutation.Remove(),
            }),
            TestContext.Current.CancellationToken);

        result.Saved.Should().BeTrue();
        result.Snapshot.Effective.Model.Should().Be("user-model");
        result.Snapshot.GetValueInfo("model").Source.Should().Be(ConfigScope.User);
    }

    [Fact]
    public async Task EnvironmentOverride_IsReportedAndNeverPersisted()
    {
        SetEnvironment("ONECODE_MODEL", "environment-model");
        using var sut = new ConfigManager(_userDir, _projectDir);

        var result = await sut.ApplyAsync(
            ConfigPatch.Set(ConfigScope.Project, "model", "project-model"),
            TestContext.Current.CancellationToken);

        result.Saved.Should().BeTrue();
        result.Snapshot.Effective.Model.Should().Be("environment-model");
        result.OverriddenChanges.Should().ContainSingle().Which.Should().Be("model");
        File.ReadAllText(ProjectSettingsPath).Should().Contain("project-model");
    }

    [Fact]
    public async Task ConcurrentPatches_AreSerializedWithoutLostUpdates()
    {
        using var sut = new ConfigManager(_userDir, _projectDir);

        var first = sut.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "model", "gpt-5.6"),
            TestContext.Current.CancellationToken);
        var second = sut.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "maxTurns", 64),
            TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second);

        using var document = JsonDocument.Parse(File.ReadAllText(UserSettingsPath));
        document.RootElement.GetProperty("model").GetString().Should().Be("gpt-5.6");
        document.RootElement.GetProperty("maxTurns").GetInt32().Should().Be(64);
    }

    [Fact]
    public async Task SessionOverride_WinsWithoutWritingFiles()
    {
        File.WriteAllText(UserSettingsPath, """{"model":"user-model"}""");
        using var sut = new ConfigManager(_userDir, _projectDir);

        var result = await sut.ApplyAsync(
            ConfigPatch.Set(ConfigScope.Session, "model", "session-model"),
            TestContext.Current.CancellationToken);

        result.Snapshot.Effective.Model.Should().Be("session-model");
        result.Snapshot.GetValueInfo("model").Source.Should().Be(ConfigScope.Session);
        File.ReadAllText(UserSettingsPath).Should().Contain("user-model");
        File.Exists(ProjectSettingsPath).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyPatch_ClassifiesActivationModes()
    {
        using var sut = new ConfigManager(_userDir, _projectDir);

        var result = await sut.ApplyAsync(
            new ConfigPatch(ConfigScope.User, new Dictionary<string, ConfigMutation>
            {
                ["showThinking"] = new ConfigMutation.Set(true),
                ["model"] = new ConfigMutation.Set("gpt-5.6"),
                ["baseUrl"] = new ConfigMutation.Set("https://api.example.com"),
            }),
            TestContext.Current.CancellationToken);

        result.ImmediateChanges.Should().ContainSingle().Which.Should().Be("showThinking");
        result.NextOperationChanges.Should().ContainSingle().Which.Should().Be("model");
        result.RestartRequiredChanges.Should().ContainSingle().Which.Should().Be("baseUrl");
    }

    [Fact]
    public async Task NestedSettings_AreFlattenedInMemoryAndExpandedOnDisk()
    {
        File.WriteAllText(UserSettingsPath, """
            {
              "autodream": {
                "enabled": false,
                "minHours": 12,
                "minSessions": 4
              },
              "goal": {
                "maxSubGoalAttempts": 7,
                "maxTurnsPerSubGoal": 30,
                "maxTotalTokens": 150000,
                "maxWallClockHours": 1.5,
                "maxCostUsd": 2.5
              }
            }
            """);
        using var sut = new ConfigManager(_userDir, _projectDir);

        sut.Current.Effective.Get("autodream.enabled", true).Should().BeFalse();
        sut.Current.Effective.Get("goal.maxSubGoalAttempts", 20).Should().Be(7);
        sut.Current.Effective.Get<long>("goal.maxTotalTokens").Should().Be(150_000L);
        sut.Current.Effective.Get<decimal>("goal.maxCostUsd").Should().Be(2.5m);

        var result = await sut.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "autodream.minHours", 8),
            TestContext.Current.CancellationToken);

        result.Saved.Should().BeTrue();
        using var document = JsonDocument.Parse(File.ReadAllText(UserSettingsPath));
        document.RootElement.TryGetProperty("autodream", out var autoDream).Should().BeTrue();
        autoDream.GetProperty("enabled").GetBoolean().Should().BeFalse();
        autoDream.GetProperty("minHours").GetInt32().Should().Be(8);
        document.RootElement.TryGetProperty("autodream.minHours", out _).Should().BeFalse();
        document.RootElement.GetProperty("goal").GetProperty("maxSubGoalAttempts").GetInt32().Should().Be(7);
    }

    [Fact]
    public void DottedJsonProperty_IsRejectedWithoutReplacingSnapshot()
    {
        File.WriteAllText(UserSettingsPath, """{"model":"original-model"}""");
        using var sut = new ConfigManager(_userDir, _projectDir);
        File.WriteAllText(UserSettingsPath, """{"autodream.enabled":false}""");

        sut.Reload();

        sut.Current.Effective.Model.Should().Be("original-model");
    }

    [Fact]
    public void UnknownJsonProperty_IsRejectedWithoutReplacingSnapshot()
    {
        File.WriteAllText(UserSettingsPath, """{"model":"original-model"}""");
        using var sut = new ConfigManager(_userDir, _projectDir);
        File.WriteAllText(UserSettingsPath, """{"unknownSetting":true}""");

        sut.Reload();

        sut.Current.Effective.Model.Should().Be("original-model");
    }

    [Fact]
    public void EnvironmentValues_AreConvertedUsingDescriptorTypes()
    {
        SetEnvironment("ONECODE_AUTODREAM", "false");
        SetEnvironment("ONECODE_AUTODREAM_MIN_HOURS", "9");

        using var sut = new ConfigManager(_userDir, _projectDir);

        sut.Current.Effective.Get("autodream.enabled", true).Should().BeFalse();
        sut.Current.Effective.Get("autodream.minHours", 0).Should().Be(9);
    }

    [Fact]
    public void Reload_MalformedJson_KeepsPreviousSnapshot()
    {
        File.WriteAllText(UserSettingsPath, """{"model":"original-model"}""");
        using var sut = new ConfigManager(_userDir, _projectDir);
        File.WriteAllText(UserSettingsPath, """{"model":BROKEN}""");

        sut.Reload();

        sut.Current.Effective.Model.Should().Be("original-model");
    }

    [Fact]
    public async Task Watcher_ValidJson_RaisesSnapshotEvent()
    {
        File.WriteAllText(UserSettingsPath, """{"model":"old-model"}""");
        using var sut = new ConfigManager(_userDir, _projectDir);
        sut.InitializeWatcher();
        var completion = new TaskCompletionSource<ConfigSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.SettingsChanged += snapshot =>
        {
            if (snapshot.Effective.Model == "new-model")
                completion.TrySetResult(snapshot);
        };

        File.WriteAllText(UserSettingsPath, """{"model":"new-model"}""");
        var snapshot = await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);

        snapshot.Effective.Model.Should().Be("new-model");
    }

    private void SetEnvironment(string name, string? value)
    {
        if (!_originalEnvironment.ContainsKey(name))
            _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }
}
