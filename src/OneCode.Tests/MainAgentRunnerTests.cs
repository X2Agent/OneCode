using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="MainAgentRunner"/>'s testable surface:
/// <list type="bullet">
/// <item><see cref="ToolNames.ReadOnlyTools"/> (public static, 统一来源)</item>
/// <item><see cref="MainModeContextProviderBuilder.ResolveAgentMode"/> (internal static)</item>
/// <item><see cref="AgentPipelineAssembly.CreateAutoApprovalRules"/> (internal instance)</item>
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
            costTracker: new CostTracker());

        var chatClient = Substitute.For<IChatClient>();
        var modelManager = new ModelManager(_configManager, new ModelCatalogStore());
        var promptManager = new PromptManager();
        var sessionManager = Substitute.For<ISessionManager>();

        var (_, mainContextBuilder) = TestSupport.TestAgentContextProviderAssembly.Create(
            sessionManager,
            modelManager,
            _modeProvider,
            promptManager,
            planWorkflowService: Substitute.For<IPlanWorkflowApplicationService>());
        _runner = new MainAgentRunner(
            mainContextBuilder,
            _pipelineAssembly,
            new CompactionProviderBuilder(chatClient, NullLoggerFactory.Instance, modelManager, new OneCode.App.Services.Compact.CompactPromptBuilder(promptManager)),
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

    // Helper: invoke CreateAutoApprovalRules via AgentPipelineAssembly.
    private List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> InvokeCreateAutoApprovalRules()
    {
        return _pipelineAssembly.CreateAutoApprovalRules();
    }

    // Helper: build a FunctionCallContent with a given tool name and (optional) args.
    private static FunctionCallContent MakeCall(string toolName, string? command = null)
    {
        var args = new Dictionary<string, object?>();
        if (command is not null)
            args["command"] = command;
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
        var ctx = new ToolAutoApprovalRuleContext(call, agent, null, Array.Empty<ChatMessage>(), null);
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
    [InlineData(PermissionMode.Bubble, "build")]
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

    // CreateAutoApprovalRules (private instance) — uses _modeProvider.CurrentMode

    [Fact]
    public async Task CreateAutoApprovalRules_BypassPermissionsMode_ApprovesAllTools()
    {
        _modeProvider.SetCurrentMode(PermissionMode.BypassPermissions);
        var rules = InvokeCreateAutoApprovalRules();

        // Read-only tools approved (always)
        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        // Non-readonly tools ALSO approved in BypassPermissions
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Edit"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("ApplyWorkspaceEdit"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /tmp/x"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("PowerShell", "Remove-Item foo"))).Should().BeTrue();
        // Unknown tools also approved
        (await EvaluateAsync(rules, MakeCall("CustomTool"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("ApplyWorkspaceEdit")]
    public async Task CreateAutoApprovalRules_AcceptEditsMode_ApprovesFileWriteTools(string tool)
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeCreateAutoApprovalRules();

        (await EvaluateAsync(rules, MakeCall(tool))).Should().BeTrue(
            $"{tool} should be auto-approved in AcceptEdits mode");
    }

    [Fact]
    public async Task CreateAutoApprovalRules_AcceptEditsMode_ApprovesReadOnlyShellCommands()
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeCreateAutoApprovalRules();

        // Read-only shell commands approved (git status is in the read-only whitelist)
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "ls -la"))).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAutoApprovalRules_AcceptEditsMode_DeniesNonReadOnlyShellCommands()
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeCreateAutoApprovalRules();

        // Destructive shell commands NOT auto-approved
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /tmp/x"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "sudo apt-get install foo"))).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAutoApprovalRules_AcceptEditsMode_DeniesUnknownDangerousTools()
    {
        _modeProvider.SetCurrentMode(PermissionMode.AcceptEdits);
        var rules = InvokeCreateAutoApprovalRules();

        // Unknown / non-shell / non-file-write tools are NOT auto-approved
        (await EvaluateAsync(rules, MakeCall("SomeUnknownTool"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("WebBrowser"))).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAutoApprovalRules_PlanMode_ApprovesReadOnlyToolsAndReadOnlyShell()
    {
        _modeProvider.SetCurrentMode(PermissionMode.Plan);
        var rules = InvokeCreateAutoApprovalRules();

        // Read-only tools approved (always)
        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Grep"))).Should().BeTrue();

        // Read-only shell approved (aligns with PlanModePermissionStrategy)
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "ls"))).Should().BeTrue();

        // Non-read-only shell and write tools denied (PermissionChecker layer decides)
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Edit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("ApplyWorkspaceEdit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("CustomTool"))).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAutoApprovalRules_DefaultMode_OnlyApprovesReadOnlyAndReadOnlyShell()
    {
        _modeProvider.SetCurrentMode(PermissionMode.Default);
        var rules = InvokeCreateAutoApprovalRules();

        // Read-only tools approved
        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Grep"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("WebFetch"))).Should().BeTrue();

        // Read-only shell approved (auto-approved via IsReadOnlyShell)
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "ls -la"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("PowerShell", "Get-ChildItem"))).Should().BeTrue();

        // File writes / destructive shell / unknown tools denied
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Edit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("ApplyWorkspaceEdit"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf /tmp/x"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("CustomTool"))).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAutoApprovalRules_DontAskMode_DeniesAllNonReadOnlyTools()
    {
        _modeProvider.SetCurrentMode(PermissionMode.DontAsk);
        var rules = InvokeCreateAutoApprovalRules();

        // Read-only approved
        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        // Non-readonly denied (no prompts in DontAsk mode)
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeFalse();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf x"))).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAutoApprovalRules_AutoMode_DeniesAllNonReadOnlyTools()
    {
        // PermissionMode.Auto behaves like Default in this rule table
        // (the YOLO classifier lives in PermissionChecker, not here).
        _modeProvider.SetCurrentMode(PermissionMode.Auto);
        var rules = InvokeCreateAutoApprovalRules();

        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        // Auto mode falls through to the Default branch — read-only shell approved
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
        (await EvaluateAsync(rules, MakeCall("Bash", "rm -rf x"))).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAutoApprovalRules_BubbleMode_DeniesAllNonReadOnlyTools()
    {
        _modeProvider.SetCurrentMode(PermissionMode.Bubble);
        var rules = InvokeCreateAutoApprovalRules();

        (await EvaluateAsync(rules, MakeCall("Read"))).Should().BeTrue();
        // In Bubble mode (default branch), non-readonly tools denied —
        // bubbling happens via PermissionChecker + BubbleHandler, not auto-approval.
        (await EvaluateAsync(rules, MakeCall("Write"))).Should().BeFalse();
        // Read-only shell still approved (default-branch behavior)
        (await EvaluateAsync(rules, MakeCall("Bash", "git status"))).Should().BeTrue();
    }

    // WrapApprovalRequiredTools — verify ToolMetadataRegistry-driven wrapping

    [Fact]
    public void WrapApprovalRequiredTools_UsesToolMetadataRegistry()
    {
        var wrapMethod = typeof(OneCode.Infrastructure.Agent.AgentPipelineBuilder).GetMethod(
            "WrapApprovalRequiredTools",
            BindingFlags.NonPublic | BindingFlags.Static);
        wrapMethod.Should().NotBeNull();

        var metadata = new OneCode.Core.Tools.ToolMetadataRegistry();
        metadata.Register(new OneCode.Core.Tools.ToolMetadata
        {
            Name = "Write",
            Risk = OneCode.Core.Tools.ToolRisk.Destructive,
            ApprovalMode = OneCode.Core.Tools.ToolApprovalMode.Always,
        });
        metadata.Register(new OneCode.Core.Tools.ToolMetadata
        {
            Name = "Read",
            Risk = OneCode.Core.Tools.ToolRisk.ReadOnly,
            ApprovalMode = OneCode.Core.Tools.ToolApprovalMode.Never,
        });

        var writeFn = AIFunctionFactory.Create(
            (string _) => "ok", "Write", "Write a file");
        var readFn = AIFunctionFactory.Create(
            (string _) => "ok", "Read", "Read a file");
        var tools = new List<AITool> { writeFn, readFn };

        var result = (IList<AITool>)wrapMethod!.Invoke(null, [tools, metadata])!;

        result.Count.Should().Be(2);
        result.Should().Contain(readFn, "Read has ApprovalMode.Never and should not be wrapped");
        result.Should().NotContain(writeFn, "Write has ApprovalMode.Always and should be wrapped");
    }
}
