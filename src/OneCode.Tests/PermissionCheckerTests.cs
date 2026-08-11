using System.Text.Json;
using OneCode.Core.Permissions;
using OneCode.Core.Permissions.Yolo;
using OneCode.Core.Tools;

namespace OneCode.Tests;

public sealed class PermissionCheckerTests
{
    private readonly PermissionChecker _sut;

    public PermissionCheckerTests()
    {
        _sut = new PermissionChecker(CreateYoloClassifier());
    }

    private static YoloClassifier CreateYoloClassifier()
    {
        var ruleStore = new YoloRuleStore(logger: null);
        ruleStore.ClearRules();
        return new YoloClassifier(ruleStore, new ToolMetadataRegistry(), logger: null);
    }

    [Fact]
    public async Task CheckAsync_BypassPermissions_AlwaysAllows()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.BypassPermissions };
        using var doc = JsonDocument.Parse("{}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_PlanMode_DeniesWriteTool()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        using var doc = JsonDocument.Parse(@"{""path"":""file.txt"",""content"":""x""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_PlanMode_AllowsReadTool()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        using var doc = JsonDocument.Parse(@"{""path"":""file.txt""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Read", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Theory]
    [InlineData(PermissionMode.Default)]
    [InlineData(PermissionMode.AcceptEdits)]
    public async Task CheckAsync_PathOutsideWorkingDir_Denied(PermissionMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "sandbox");
        var ctx = new ToolPermissionContext
        {
            Mode = mode,
            WorkingDirectory = workDir,
        };
        var outsidePath = Path.GetTempPath();
        using var doc = JsonDocument.Parse($@"{{""path"":""{outsidePath.Replace("\\", "\\\\")}"",""content"":""x""}}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_WorkingDirectoryUsedForPathValidation_NotEnvCurrentDir()
    {
        var ct = TestContext.Current.CancellationToken;
        // Session working directory is different from Environment.CurrentDirectory
        var sessionWorkDir = Path.Combine(Path.GetTempPath(), "my-session");
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Default,
            WorkingDirectory = sessionWorkDir,
        };
        var pathInsideSession = Path.Combine(sessionWorkDir, "file.txt");
        using var doc = JsonDocument.Parse(
            $@"{{""path"":""{pathInsideSession.Replace("\\", "\\\\")}"",""content"":""x""}}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        // Should Ask (not Deny) because path is within session working dir
        result.Decision.Should().NotBe(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_AlwaysAllowRule_OverridesDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Default,
            WorkingDirectory = Path.GetTempPath(),
            RulesBySource = new Dictionary<string, PermissionRuleGroup>
            {
                ["test"] = new PermissionRuleGroup(
                    AlwaysAllow: [new PermissionRule("Bash", "git status")])
            },
        };
        using var doc = JsonDocument.Parse(@"{""command"":""git status""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_AlwaysDenyRule_DeniesMatchingCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Auto,
            WorkingDirectory = Path.GetTempPath(),
            RulesBySource = new Dictionary<string, PermissionRuleGroup>
            {
                ["test"] = new PermissionRuleGroup(
                    AlwaysDeny: [new PermissionRule("Bash", "rm *")])
            },
        };
        using var doc = JsonDocument.Parse(@"{""command"":""rm *""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_BubbleMode_ReadOnlyToolsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Bubble,
            WorkingDirectory = Path.GetTempPath(),
        };
        using var doc = JsonDocument.Parse(@"{""path"":""file.txt""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Read", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_BubbleMode_WriteWithoutRules_ReturnsAskWithBubbleRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Bubble,
            WorkingDirectory = Path.GetTempPath(),
        };
        using var doc = JsonDocument.Parse(@"{""path"":""file.txt"",""content"":""x""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Ask);
        result.DecisionReason.Should().NotBeNull();
        result.DecisionReason.Should().BeOfType<PermissionDecisionReason.BubbleRequest>();
        var bubbleReason = (PermissionDecisionReason.BubbleRequest)result.DecisionReason!;
        bubbleReason.ToolName.Should().Be("Write");
    }

    [Fact]
    public async Task CheckAsync_BubbleMode_AlwaysAllowRule_OverridesBubble()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Bubble,
            WorkingDirectory = Path.GetTempPath(),
            RulesBySource = new Dictionary<string, PermissionRuleGroup>
            {
                ["test"] = new PermissionRuleGroup(
                    AlwaysAllow: [new PermissionRule("Write", "safe/*")])
            },
        };
        using var doc = JsonDocument.Parse(@"{""path"":""safe/file.txt"",""content"":""x""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_BubbleMode_AlwaysDenyRule_Denies()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Bubble,
            WorkingDirectory = Path.GetTempPath(),
            RulesBySource = new Dictionary<string, PermissionRuleGroup>
            {
                ["test"] = new PermissionRuleGroup(
                    AlwaysDeny: [new PermissionRule("Bash", "rm *")])
            },
        };
        using var doc = JsonDocument.Parse(@"{""command"":""rm *""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_BubbleMode_PathOutsideWorkingDir_Denied()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "sandbox");
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Bubble,
            WorkingDirectory = workDir,
        };
        var outsidePath = Path.GetTempPath();
        using var doc = JsonDocument.Parse($@"{{""path"":""{outsidePath.Replace("\\", "\\\\")}"",""content"":""x""}}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_DontAskMode_UnknownTool_DeniesInsteadOfAsking()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.DontAsk,
            WorkingDirectory = Path.GetTempPath(),
        };
        using var doc = JsonDocument.Parse(@"{""command"":""something""}");
        var input = doc.RootElement;

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }
}
