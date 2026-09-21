using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.Core.Hooks;
using OneCode.Core.Build;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="MainAgentRunner"/>'s testable surface:
/// <list type="bullet">
/// <item><see cref="ToolNames.ReadOnlyTools"/> (public static, 统一来源)</item>
/// <item><see cref="MainModeContextProviderBuilder.ResolveAgentMode"/> (internal static)</item>
/// <item><see cref="AutoApprovalRulesFactory"/> (internal static)</item>
/// </list>
/// The full <c>RunAsync</c> / <c>RunStreamingAsync</c> pipeline is exercised
/// by integration tests; here we focus on the mode-aware auto-approval rule
/// table which is the part with non-trivial branching.
/// </summary>
public sealed class MainAgentRunnerTests : IDisposable
{
    private readonly string _configDir;
    private readonly ConfigManager _configManager;
    private readonly PermissionModeProvider _modeProvider;
    private readonly AgentPipelineAssembly _pipelineAssembly;
    private readonly MainAgentRunner _runner;

    public MainAgentRunnerTests()
    {
        _configDir = Path.Combine(
            Path.GetTempPath(),
            "OneCodeMainAgentRunnerTests-" + Guid.NewGuid().ToString("N")[..8]);
        _configManager = new ConfigManager(_configDir);
        _modeProvider = new PermissionModeProvider(_configManager);

        _pipelineAssembly = new AgentPipelineAssembly(
            workingDirectoryAccessor: Substitute.For<IWorkingDirectoryAccessor>(),
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            verificationProvider: Substitute.For<IVerificationProvider>(),
            modeProvider: _modeProvider,
            permissionChecker: Substitute.For<IPermissionChecker>(),
            tokenLedger: new TokenLedger());

        var chatClient = Substitute.For<IChatClient>();
        var modelManager = new ModelManager(_configManager, new ModelCatalogStore());
        var promptManager = new PromptManager();
        var sessionManager = Substitute.For<ISessionManager>();

        var (sharedBuilder, mainContextBuilder) = TestSupport.TestAgentContextProviderAssembly.Create(
            sessionManager,
            modelManager,
            _modeProvider,
            promptManager,
            planWorkflowService: Substitute.For<IPlanWorkflowApplicationService>());
        _runner = new MainAgentRunner(
            new AgentContextPipeline(sharedBuilder, mainContextBuilder),
            _pipelineAssembly,
            new CompactionStrategyFactory(chatClient, modelManager, new OneCode.App.Services.Compact.CompactPromptBuilder(promptManager)),
            new AgentSessionStore(null, NullLogger<AgentSessionStore>.Instance),
            chatClient,
            NullLoggerFactory.Instance,
            Substitute.For<IServiceProvider>(),
            new ToolMetadataRegistry());
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
            Directory.Delete(_configDir, recursive: true);
    }

    // Helper: invoke the single auto-approval rule source AutoApprovalRulesFactory.
    private List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> InvokeAutoApprovalRules()
    {
        return AutoApprovalRulesFactory.Create(_modeProvider.CurrentMode);
    }

    [Theory]
    [InlineData(BuildValidationStatus.Skipped, false)]
    [InlineData(BuildValidationStatus.Passed, false)]
    [InlineData(BuildValidationStatus.Failed, true)]
    public void ShouldRollBackForValidation_OnlyFailedTriggersRollback(
        BuildValidationStatus status, bool expected)
    {
        // 回归守护：旧实现按 != Passed 判定，验证 Skipped（如生成的 HTML 报告
        // 无匹配验证 profile）会被当失败处理，静默删除已写入的文件。
        MainAgentRunner.ShouldRollBackForValidation(status).Should().Be(expected);
    }

    // Helper: build a FunctionCallContent with a given tool name and (optional) args.
    private static FunctionCallContent MakeCall(string toolName, string? command = null, string? shell = null)
    {
        var args = new Dictionary<string, object?>();
        if (command is not null)
            args["command"] = command;
        if (shell is not null)
            args["shell"] = shell;
        return new FunctionCallContent(
            callId: "test-call",
            name: toolName,
            arguments: args!);
    }

    private static async Task<bool> EvaluateAsync(
        List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> rules,
        FunctionCallContent call)
    {
        var agent = Substitute.For<AIAgent>();
        var ctx = new ToolAutoApprovalRuleContext(call, agent, null, [], null);
        foreach (var rule in rules)
            if (await rule(ctx).ConfigureAwait(false))
                return true;
        return false;
    }

    // ResolveAgentMode — internal static

    [Theory]
    [InlineData(WorkingMode.Plan, PermissionMode.Default, "plan")]
    [InlineData(WorkingMode.Plan, PermissionMode.Plan, "plan")]
    [InlineData(WorkingMode.Plan, PermissionMode.BypassPermissions, "plan")] // WorkingMode wins
    [InlineData(WorkingMode.Plan, null, "plan")]
    [InlineData(WorkingMode.Team, PermissionMode.Default, "team")]
    [InlineData(WorkingMode.Team, null, "team")]
    [InlineData(WorkingMode.Team, PermissionMode.Plan, "team")] // WorkingMode wins
    [InlineData(WorkingMode.Goal, PermissionMode.Default, "goal")]
    [InlineData(WorkingMode.Goal, null, "goal")]
    [InlineData(WorkingMode.Goal, PermissionMode.Plan, "goal")] // WorkingMode wins
    public void ResolveAgentMode_NonBuildWorkingModes_TakePrecedenceOverPermissionMode(
        WorkingMode workingMode, PermissionMode? permissionMode, string expected)
    {
        // Plan/Team/Goal working modes always win over permissionMode.
        var actual = MainModeContextProviderBuilder.ResolveAgentMode(workingMode, permissionMode);
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(PermissionMode.Plan, "plan")]
    [InlineData(PermissionMode.Default, "build")]
    [InlineData(PermissionMode.AcceptEdits, "build")]
    [InlineData(PermissionMode.BypassPermissions, "build")]
    [InlineData(PermissionMode.DontAsk, "build")]
    [InlineData(PermissionMode.Auto, "build")]
    [InlineData(null, "build")]
    public void ResolveAgentMode_WhenWorkingModeIsBuild_FallsBackToPermissionMode(
        PermissionMode? permissionMode, string expected)
    {
        // WorkingMode.Build (default) defers to permissionMode to switch build→plan.
        // Only PermissionMode.Plan flips the resolved mode to "plan"; all other
        // permission modes keep it as "build".
        var actual = MainModeContextProviderBuilder.ResolveAgentMode(WorkingMode.Build, permissionMode);
        actual.Should().Be(expected);
    }

    // AutoApprovalRulesFactory.Create — uses _modeProvider.CurrentMode

    [Fact]
    public async Task AutoApprovalRulesFactory_BypassPermissionsMode_ApprovesAllTools()
    {
        _modeProvider.SetCurrentMode(PermissionMode.BypassPermissions);
        var rules = InvokeAutoApprovalRules();

        // Read-only tools approved (always)
        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        // Non-readonly tools ALSO approved in BypassPermissions
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Edit"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("ApplyWorkspaceEdit"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /tmp/x"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "Remove-Item foo", shell: "powershell"))).Should().BeTrue();
        // Unknown tools also approved
        (await EvaluateAsync(rules, MakeCall("CustomTool"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("ApplyWorkspaceEdit")]
    public async Task AutoApprovalRulesFactory_AcceptEditsMode_ApprovesFileWriteTools(string tool)
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeAutoApprovalRules();

        (await EvaluateAsync(rules, MakeCall(tool))).Should().BeTrue(
            $"{tool} should be auto-approved in AcceptEdits mode");
    }

    [Fact]
    public async Task AutoApprovalRulesFactory_AcceptEditsMode_ApprovesReadOnlyShellCommands()
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeAutoApprovalRules();

        // Read-only shell commands approved (git status is in the read-only whitelist)
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "ls -la"))).Should().BeTrue();
    }

    [Fact]
    public async Task AutoApprovalRulesFactory_AcceptEditsMode_ApprovesNonDestructiveShell()
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeAutoApprovalRules();

        // AcceptEdits: non-destructive shell ⇒ Check Allow ⇒ auto-approve (B1-det).
        (await EvaluateAsync(rules, MakeCall("Bash", "sudo apt-get install foo"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "npm install"))).Should().BeTrue();

        // Destructive shell falls through to EvaluateRules ⇒ Ask ⇒ not auto-approved.
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /tmp/x"))).Should().BeFalse();
    }

    [Fact]
    public async Task AutoApprovalRulesFactory_AcceptEditsMode_DeniesUnknownDangerousTools()
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeAutoApprovalRules();

        // Unknown / non-shell / non-file-write tools are NOT auto-approved
        (await EvaluateAsync(rules, MakeCall("SomeUnknownTool"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("WebBrowser"))).Should().BeFalse();
    }

    [Fact]
    public async Task AutoApprovalRulesFactory_PlanMode_ApprovesWhenPermissionCheckAllows()
    {
        _modeProvider.SetCurrentMode(PermissionMode.Plan);
        var rules = InvokeAutoApprovalRules();

        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Grep"))).Should().BeTrue();

        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "ls"))).Should().BeTrue();

        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Edit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("ApplyWorkspaceEdit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("CustomTool"))).Should().BeFalse();
    }
    [Fact]
    public async Task AutoApprovalRulesFactory_DefaultMode_ApprovesWhenPermissionCheckAllows()
    {
        _modeProvider.SetCurrentMode(PermissionMode.Default);
        var rules = InvokeAutoApprovalRules();

        // B1-det: Allow from PermissionProfiles.Check ⇒ MAF auto-approve.
        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Grep"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("WebFetch"))).Should().BeTrue();

        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "ls -la"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "Get-ChildItem", shell: "powershell"))).Should().BeTrue();

        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Edit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("ApplyWorkspaceEdit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /tmp/x"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("CustomTool"))).Should().BeFalse();
    }
    [Fact]
    public async Task AutoApprovalRulesFactory_DontAskMode_DeniesUnlessRulesAllow()
    {
        _modeProvider.SetCurrentMode(PermissionMode.DontAsk);
        var rules = InvokeAutoApprovalRules();

        // 只读工具在 Layer 1 已 Allow；MAF 规则层不单独放行。
        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeFalse();
        // Non-readonly denied (no prompts in DontAsk mode)
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf x"))).Should().BeFalse();
    }

    [Fact]
    public async Task AutoApprovalRulesFactory_AutoMode_ApprovesWhenPermissionCheckAllows()
    {
        // Deterministic Check only — YOLO stays in PermissionChecker, not Factory.
        _modeProvider.SetCurrentMode(PermissionMode.Auto);
        var rules = InvokeAutoApprovalRules();

        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf x"))).Should().BeFalse();
    }

}
