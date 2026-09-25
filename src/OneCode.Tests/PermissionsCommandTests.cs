using NSubstitute;
using OneCode.App.Commands;
using OneCode.Core.Commands;
using OneCode.Core.Config;
using OneCode.Core.Permissions;

namespace OneCode.Tests;

/// <summary>
/// /permissions 的档位白名单与 --session 作用域契约。
/// plan / team / goalAuto 由 WorkingModeBridge 从工作模式派生，直接写入会造成
/// 权限轴与模式轴不一致的半状态（plan 白名单生效但 SubmitPlan 被拒、跨会话泄漏），
/// 故命令层必须拒绝；--session 只设运行时覆盖、不写配置——一次性全放行
/// （对话内 bypass）不得泄漏到之后的会话。
/// </summary>
public sealed class PermissionsCommandTests
{
    [Theory]
    [InlineData("plan")]
    [InlineData("Plan")]
    [InlineData("team")]
    [InlineData("goalAuto")]
    [InlineData("goal-auto")]
    public async Task ExecuteAsync_ModeDerivedValue_IsRejectedWithWorkingModeHint(string modeArg)
    {
        var (command, config, modes) = CreateCommand(PermissionMode.AcceptEdits);

        var result = await command.ExecuteAsync([modeArg], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.ErrorResult>();
        ((CommandResult.ErrorResult)result).Message.Should().Contain("working-mode-derived");
        await config.DidNotReceive().ApplyAsync(Arg.Any<ConfigPatch>(), Arg.Any<CancellationToken>());
        modes.DidNotReceive().SetCurrentMode(Arg.Any<PermissionMode?>());
    }

    [Fact]
    public async Task ExecuteAsync_SessionFlag_SetsRuntimeOverrideWithoutPersisting()
    {
        var (command, config, modes) = CreateCommand(PermissionMode.AcceptEdits);

        var result = await command.ExecuteAsync(["bypass", "--session"], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        ((CommandResult.TextResult)result).Value.Should().Contain("session only");
        modes.Received(1).SetCurrentMode(PermissionMode.BypassPermissions);
        await config.DidNotReceive().ApplyAsync(Arg.Any<ConfigPatch>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutSessionFlag_PersistsModeAndSetsRuntimeOverride()
    {
        var (command, config, modes) = CreateCommand(PermissionMode.AcceptEdits);
        ConfigureApplySaves(config);

        var result = await command.ExecuteAsync(["dontAsk"], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        modes.Received(1).SetCurrentMode(PermissionMode.DontAsk);
        await config.Received(1).ApplyAsync(
            Arg.Is<ConfigPatch>(patch => PatchSetsPermissionMode(patch, "DontAsk")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UnknownMode_ReturnsErrorWithoutTouchingState()
    {
        var (command, config, modes) = CreateCommand(PermissionMode.AcceptEdits);

        var result = await command.ExecuteAsync(["yolo"], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.ErrorResult>();
        ((CommandResult.ErrorResult)result).Message.Should().Contain("Unknown permission mode");
        await config.DidNotReceive().ApplyAsync(Arg.Any<ConfigPatch>(), Arg.Any<CancellationToken>());
        modes.DidNotReceive().SetCurrentMode(Arg.Any<PermissionMode?>());
    }

    [Fact]
    public async Task ExecuteAsync_SessionFlagWithoutMode_ReturnsUsageError()
    {
        var (command, config, modes) = CreateCommand(PermissionMode.AcceptEdits);

        var result = await command.ExecuteAsync(["--session"], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.ErrorResult>();
        modes.DidNotReceive().SetCurrentMode(Arg.Any<PermissionMode?>());
        await config.DidNotReceive().ApplyAsync(Arg.Any<ConfigPatch>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AliasedMode_ParsesToEnumValueAndPersists()
    {
        var (command, config, modes) = CreateCommand(PermissionMode.Default);
        ConfigureApplySaves(config);

        var result = await command.ExecuteAsync(["accept-edits"], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        modes.Received(1).SetCurrentMode(PermissionMode.AcceptEdits);
    }

    [Fact]
    public async Task ExecuteAsync_NoArguments_ShowsActiveOverrideAndSessionHint()
    {
        // 运行时覆盖与持久化配置不同（命令只覆盖了本次会话），状态行必须把两者都显示出来。
        var (command, _, _) = CreateCommand(PermissionMode.BypassPermissions);

        var result = await command.ExecuteAsync([], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        var text = ((CommandResult.TextResult)result).Value;
        text.Should().Contain("Mode=BypassPermissions");
        text.Should().Contain("config: default");
        text.Should().Contain("--session");
    }

    private static (PermissionsCommand Command, IConfigManager Config, IPermissionModeProvider Modes)
        CreateCommand(PermissionMode currentMode)
    {
        var modes = Substitute.For<IPermissionModeProvider>();
        modes.CurrentMode.Returns(currentMode);
        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        return (new PermissionsCommand(modes, config), config, modes);
    }

    private static void ConfigureApplySaves(IConfigManager config)
    {
        var saved = new ConfigApplyResult(
            Saved: true,
            Snapshot: ConfigSnapshot.FromEffective(new AppSettings()),
            ImmediateChanges: [],
            NextOperationChanges: [],
            RestartRequiredChanges: [],
            OverriddenChanges: [],
            Error: null);
        config.ApplyAsync(Arg.Any<ConfigPatch>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(saved));
    }

    private static bool PatchSetsPermissionMode(ConfigPatch patch, string expectedMode)
    {
        if (patch.TargetScope != ConfigScope.User)
            return false;
        if (!patch.Changes.TryGetValue("permissionMode", out var mutation))
            return false;
        return mutation is ConfigMutation.Set set && Equals(set.Value, expectedMode);
    }
}
