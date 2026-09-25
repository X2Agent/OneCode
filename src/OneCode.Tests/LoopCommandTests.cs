using OneCode.App.Commands;
using OneCode.Core.Commands;
using OneCode.Core.Config;
using OneCode.Core.Permissions;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// <c>/loop</c> 命令的参数解析与权限门禁。运行时循环本身的语义由
/// <see cref="GoalSubGoalLoopTests"/> 与 IterativeLoopService 的守卫测试覆盖。
/// </summary>
public sealed class LoopCommandTests
{
    [Fact]
    public async Task ExecuteAsync_NoArguments_ReturnsUsageError()
    {
        var result = await CreateCommand(PermissionMode.GoalAuto).ExecuteAsync([], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.ErrorResult>();
    }

    [Fact]
    public async Task ExecuteAsync_TaskOnly_ReturnsLoopResultWithConfiguredIterations()
    {
        var config = CreateConfigManager();
        var command = CreateCommand(PermissionMode.GoalAuto, config);

        var loop = await ExecuteAsync(command, ["修复登录页样式回归"]);

        loop.Task.Should().Be("修复登录页样式回归");
        loop.CheckCommand.Should().BeNull();
        loop.MaxIterations.Should().Be(3, "未指定 --max 时取自 loop.maxIterations 默认值");
    }

    [Fact]
    public async Task ExecuteAsync_ConfiguredIterations_OverridesDefaultWhenMaxOmitted()
    {
        var config = CreateConfigManager(new AppSettings(new Dictionary<string, object?>
        {
            ["loop.maxIterations"] = 7,
        }));

        var loop = await ExecuteAsync(CreateCommand(PermissionMode.DontAsk, config), ["跑通测试"]);

        loop.MaxIterations.Should().Be(7);
    }

    [Fact]
    public async Task ExecuteAsync_CheckAndMaxFlags_AreExtractedFromTask()
    {
        var loop = await ExecuteAsync(
            CreateCommand(PermissionMode.GoalAuto),
            ["修复", "登录", "回归", "--check", "npm test", "--max", "5"]);

        loop.Task.Should().Be("修复 登录 回归");
        loop.CheckCommand.Should().Be("npm test");
        loop.MaxIterations.Should().Be(5);
    }

    [Theory]
    [InlineData(new object[] { new[] { "task", "--check" } })]
    [InlineData(new object[] { new[] { "task", "--check", "  " } })]
    [InlineData(new object[] { new[] { "task", "--max" } })]
    [InlineData(new object[] { new[] { "task", "--max", "abc" } })]
    [InlineData(new object[] { new[] { "task", "--max", "0" } })]
    public async Task ExecuteAsync_MalformedFlags_ReturnErrorWithoutRunningLoop(string[] args)
    {
        var command = CreateCommand(PermissionMode.GoalAuto);

        var result = await command.ExecuteAsync(args, TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.ErrorResult>();
        result.Should().NotBeOfType<CommandResult.LoopResult>();
    }

    [Fact]
    public async Task ExecuteAsync_MaxAboveUpperBound_ReturnsErrorNamingTheBound()
    {
        // 反证点：--max 无上界时，一次误输入会真的启动上万次自主 agent run。
        var command = CreateCommand(PermissionMode.GoalAuto);

        var result = await command.ExecuteAsync(
            ["跑通构建", "--max", "999"],
            TestContext.Current.CancellationToken);

        var error = result.Should().BeOfType<CommandResult.ErrorResult>().Subject;
        error.Message.Should().Contain(ModeBudgetSettings.MaxLoopIterationsUpperBound.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        result.Should().NotBeOfType<CommandResult.LoopResult>();
    }

    [Fact]
    public async Task ExecuteAsync_ConfiguredIterationsAboveUpperBound_IsClamped()
    {
        var config = CreateConfigManager(new AppSettings(new Dictionary<string, object?>
        {
            ["loop.maxIterations"] = 10000,
        }));

        var loop = await ExecuteAsync(CreateCommand(PermissionMode.GoalAuto, config), ["跑通测试"]);

        loop.MaxIterations.Should().Be(ModeBudgetSettings.MaxLoopIterationsUpperBound);
    }

    [Theory]
    [InlineData(PermissionMode.Default)]
    [InlineData(PermissionMode.Plan)]
    [InlineData(PermissionMode.AcceptEdits)]
    public async Task ExecuteAsync_InteractivePermissionMode_RefusesToRunLoop(PermissionMode mode)
    {
        // 反证点：循环是自主执行体，在交互审批模式下拒绝而不是静默放权。
        var command = CreateCommand(mode);

        var result = await command.ExecuteAsync(["跑通构建"], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.ErrorResult>();
        ((CommandResult.ErrorResult)result).Message.Should().Contain(mode.ToString());
    }

    [Theory]
    [InlineData(PermissionMode.GoalAuto)]
    [InlineData(PermissionMode.DontAsk)]
    [InlineData(PermissionMode.Auto)]
    [InlineData(PermissionMode.BypassPermissions)]
    public async Task ExecuteAsync_AutonomousPermissionMode_ReturnsLoopResult(PermissionMode mode)
    {
        var result = await ExecuteAsync(CreateCommand(mode), ["跑通构建"]);

        result.Task.Should().Be("跑通构建");
    }

    private static async Task<CommandResult.LoopResult> ExecuteAsync(LoopCommand command, string[] args)
    {
        var result = await command.ExecuteAsync(args, TestContext.Current.CancellationToken);
        result.Should().BeOfType<CommandResult.LoopResult>();
        return (CommandResult.LoopResult)result;
    }

    private static LoopCommand CreateCommand(
        PermissionMode mode,
        IConfigManager? configManager = null)
    {
        var modes = Substitute.For<IPermissionModeProvider>();
        modes.CurrentMode.Returns(mode);
        return new LoopCommand(modes, configManager ?? CreateConfigManager());
    }

    private static IConfigManager CreateConfigManager(AppSettings? settings = null)
    {
        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(settings ?? new AppSettings()));
        return config;
    }
}
