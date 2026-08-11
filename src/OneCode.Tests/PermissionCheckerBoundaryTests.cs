using System.Text.Json;
using OneCode.Core.Permissions;
using OneCode.Core.Permissions.Yolo;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Boundary tests for PermissionChecker — supplements PermissionCheckerTests with
/// Plan mode SavePlan/SubmitPlan, nested paths, empty inputs,
/// and behavior differences across PermissionMode values.
/// </summary>
public sealed class PermissionCheckerBoundaryTests
{
    private readonly PermissionChecker _sut;

    public PermissionCheckerBoundaryTests()
    {
        _sut = new PermissionChecker(CreateYoloClassifier());
    }

    private static YoloClassifier CreateYoloClassifier()
    {
        var ruleStore = new YoloRuleStore(logger: null);
        ruleStore.ClearRules();
        return new YoloClassifier(ruleStore, new ToolMetadataRegistry(), logger: null);
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // Plan mode — SavePlan/SubmitPlan

    [Fact]
    public async Task CheckAsync_PlanMode_SavePlan_IsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        var input = ParseJson(@"{""plan"":""step 1""}");

        var result = await _sut.CheckAsync("SavePlan", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow,
            "SavePlan is one of the file-write tools permitted in plan mode (previous regression)");
    }

    [Fact]
    public async Task CheckAsync_PlanMode_SubmitPlan_IsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        var input = ParseJson(@"{""plan"":""step 1""}");

        var result = await _sut.CheckAsync("SubmitPlan", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow,
            "SubmitPlan is one of the file-write tools permitted in plan mode (previous regression)");
    }

    [Theory]
    [InlineData("Task")]
    public async Task CheckAsync_PlanMode_TaskManagementTools_AreAllowed(string toolName)
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        var input = ParseJson("{}");

        var result = await _sut.CheckAsync(toolName, input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_PlanMode_EditTool_IsDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        var input = ParseJson(@"{""file_path"":""a.txt"",""old"":""x"",""new"":""y""}");

        var result = await _sut.CheckAsync("Edit", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
        result.Message.Should().Contain("plan mode");
    }

    [Fact]
    public async Task CheckAsync_PlanMode_Write_IsDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        var input = ParseJson(@"{""file_path"":""/tmp/test.txt""}");

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_PlanMode_BashNonReadOnly_IsDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        var input = ParseJson(@"{""command"":""rm -rf /""}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_PlanMode_ReadOnlyBashCommand_IsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        // "ls" is classified as read-only by BashCommandClassifier
        var input = ParseJson(@"{""command"":""ls""}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_PlanMode_ReadTool_OutsideWorkingDir_IsDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "plan_sandbox");
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan, WorkingDirectory = workDir };
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside_secret.txt");
        var input = ParseJson($@"{{""path"":""{outsidePath.Replace("\\", "\\\\")}""}}");

        var result = await _sut.CheckAsync("Read", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny,
            "Plan mode must validate paths for read-only tools to prevent reading files outside the working directory");
    }

    // Nested path permission checks

    [Fact]
    public async Task CheckAsync_DeeplyNestedPath_InsideWorkingDir_IsAllowedInAcceptEdits()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "project_root");
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.AcceptEdits,
            WorkingDirectory = workDir,
        };
        var input = ParseJson(@"{""file_path"":""a/b/c/d/e/f/file.txt"",""content"":""x""}");

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_NestedPathTraversal_OutsideWorkingDir_IsDeniedInAcceptEdits()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "deep_sandbox");
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.AcceptEdits,
            WorkingDirectory = workDir,
        };
        var input = ParseJson(@"{""file_path"":""a/b/../../../outside.txt"",""content"":""x""}");

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_NestedPathInsideWorkingDir_DefaultMode_ReturnsAskOrAllow()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "default_mode_test");
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Default,
            WorkingDirectory = workDir,
        };
        var input = ParseJson(@"{""file_path"":""src/sub/deep/file.txt"",""content"":""x""}");

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        // Should NOT be denied — path is within working dir, so it falls through to Ask
        result.Decision.Should().NotBe(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_PrefixSpoofingSiblingDir_IsDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        // C:\Temp\app vs C:\Temp\application — must be strictly within, not prefix
        var workDir = Path.Combine(Path.GetTempPath(), "app");
        Directory.CreateDirectory(workDir);
        var siblingDir = Path.Combine(Path.GetTempPath(), "application");
        Directory.CreateDirectory(siblingDir);
        try
        {
            var ctx = new ToolPermissionContext
            {
                Mode = PermissionMode.Default,
                WorkingDirectory = workDir,
            };
            var input = ParseJson($@"{{""file_path"":""{siblingDir.Replace("\\", "\\\\")}/file.txt"",""content"":""x""}}");

            var result = await _sut.CheckAsync("Write", input, ctx, ct);

            result.Decision.Should().Be(PermissionDecision.Deny,
                "prefix-spoofing sibling directory must not pass traversal check");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
            try { Directory.Delete(siblingDir, recursive: true); } catch { }
        }
    }

    // Empty / null inputs

    [Fact]
    public async Task CheckAsync_EmptyObjectInput_DefaultMode_AskDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Default,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson("{}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        // Empty object → empty command → not classified as read-only → falls through to Ask
        result.Decision.Should().Be(PermissionDecision.Ask);
    }

    [Fact]
    public async Task CheckAsync_EmptyToolName_DefaultMode_AskDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Default,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson(@"{""command"":""ls""}");

        var result = await _sut.CheckAsync("", input, ctx, ct);

        // Empty tool name — no rule match, falls through to Ask
        result.Decision.Should().NotBe(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_EmptyInput_BypassMode_AlwaysAllow()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.BypassPermissions };
        var input = ParseJson("{}");

        var result = await _sut.CheckAsync("", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow,
            "bypass mode should allow everything regardless of input");
    }

    [Fact]
    public async Task CheckAsync_EmptyInput_PlanMode_DeniesUnknownTool()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        var input = ParseJson("{}");

        var result = await _sut.CheckAsync("", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public async Task CheckAsync_UnknownTool_DefaultMode_AskDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Default,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson("{}");

        var result = await _sut.CheckAsync("MysteryTool", input, ctx, ct);

        // Unknown (non-read, non-write) tool falls through to rule evaluation → Ask
        result.Decision.Should().Be(PermissionDecision.Ask);
    }

    // Behavior differences across PermissionMode

    [Fact]
    public async Task CheckAsync_SameWriteTool_BehavesDifferentlyAcrossModes()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "mode_diff_test");
        Directory.CreateDirectory(workDir);
        try
        {
            var ctx = new ToolPermissionContext
            {
                Mode = PermissionMode.Default,
                WorkingDirectory = workDir,
            };
            var input = ParseJson(@"{""file_path"":""file.txt"",""content"":""x""}");

            // Default → Ask (no rule matched)
            var defaultResult = await _sut.CheckAsync("Write", input, ctx, ct);
            defaultResult.Decision.Should().Be(PermissionDecision.Ask);

            // Bypass → Allow
            ctx = ctx with { Mode = PermissionMode.BypassPermissions };
            var bypassResult = await _sut.CheckAsync("Write", input, ctx, ct);
            bypassResult.Decision.Should().Be(PermissionDecision.Allow);

            // Plan → Deny
            ctx = ctx with { Mode = PermissionMode.Plan };
            var planResult = await _sut.CheckAsync("Write", input, ctx, ct);
            planResult.Decision.Should().Be(PermissionDecision.Deny);

            // AcceptEdits → Allow
            ctx = ctx with { Mode = PermissionMode.AcceptEdits };
            var acceptResult = await _sut.CheckAsync("Write", input, ctx, ct);
            acceptResult.Decision.Should().Be(PermissionDecision.Allow);

            // DontAsk → Deny (no rule matched, Ask gets converted to Deny)
            ctx = ctx with { Mode = PermissionMode.DontAsk };
            var dontAskResult = await _sut.CheckAsync("Write", input, ctx, ct);
            dontAskResult.Decision.Should().Be(PermissionDecision.Deny);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CheckAsync_BypassMode_PathOutsideWorkingDir_StillAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "bypass_sandbox");
        Directory.CreateDirectory(workDir);
        try
        {
            var ctx = new ToolPermissionContext
            {
                Mode = PermissionMode.BypassPermissions,
                WorkingDirectory = workDir,
            };
            var outsidePath = Path.GetTempPath();
            var input = ParseJson($@"{{""file_path"":""{outsidePath.Replace("\\", "\\\\")}/file.txt"",""content"":""x""}}");

            var result = await _sut.CheckAsync("Write", input, ctx, ct);

            // Bypass mode skips ALL checks including path traversal
            result.Decision.Should().Be(PermissionDecision.Allow);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CheckAsync_DontAskMode_AlwaysAllowRule_OverridesDenyDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.DontAsk,
            WorkingDirectory = Path.GetTempPath(),
            RulesBySource = new Dictionary<string, PermissionRuleGroup>
            {
                ["test"] = new PermissionRuleGroup(
                    AlwaysAllow: [new PermissionRule("Bash", "git *")])
            },
        };
        var input = ParseJson(@"{""command"":""git status""}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    // AutoMode — YOLO path + profile fallback for unmatched tools

    [Fact]
    public async Task CheckAsync_AutoMode_ReadTool_Allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Auto,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson(@"{""path"":""file.txt""}");

        var result = await _sut.CheckAsync("Read", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_AutoMode_BashNonReadOnly_AskDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Auto,
            WorkingDirectory = Path.GetTempPath(),
        };
        // dotnet build: 非只读命令，不引用路径（不会触发 ValidatePath 越界 Deny），
        // YOLO None → Auto profile EvaluateRules → 无规则 → Ask
        var input = ParseJson(@"{""command"":""dotnet build""}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Ask);
    }

    // GoalAuto mode — autonomous with safety boundaries

    [Fact]
    public async Task CheckAsync_GoalAutoMode_DestructiveShell_IsDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.GoalAuto,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson(@"{""command"":""rm -rf /""}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
        result.Message.Should().Contain("GOAL mode");
    }

    [Fact]
    public async Task CheckAsync_GoalAutoMode_WriteInsideWorkDir_IsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "goal_auto_test");
        var ctx = new ToolPermissionContext { Mode = PermissionMode.GoalAuto, WorkingDirectory = workDir };
        var input = ParseJson(@"{""file_path"":""file.txt"",""content"":""x""}");

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_GoalAutoMode_UnknownTool_IsAllowedWithPathValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.GoalAuto,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson("{}");

        var result = await _sut.CheckAsync("MysteryTool", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow,
            "GoalAuto allows unknown tools after path validation (autonomous execution)");
    }

    // Team mode — AcceptEdits-like with event-driven approval for dangerous shell

    [Fact]
    public async Task CheckAsync_TeamMode_DestructiveShell_ReturnsAsk()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Team,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson(@"{""command"":""rm -rf /""}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Ask);
        result.Message.Should().Contain("Team mode");
    }

    [Fact]
    public async Task CheckAsync_TeamMode_UnknownTool_FallsBackToEvaluateRules()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Team };
        var input = ParseJson("{}");

        var result = await _sut.CheckAsync("SomeUnknownTool", input, ctx, ct);

        // No matching rules → EvaluateRules returns Ask
        result.Decision.Should().Be(PermissionDecision.Ask);
    }

    [Fact]
    public async Task CheckAsync_TeamMode_WriteInsideWorkDir_IsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "team_mode_test");
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Team, WorkingDirectory = workDir };
        var input = ParseJson(@"{""file_path"":""file.txt"",""content"":""x""}");

        var result = await _sut.CheckAsync("Write", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    // AcceptEdits — destructive shell falls through to EvaluateRules (Ask when no rule)

    [Fact]
    public async Task CheckAsync_AcceptEditsMode_DestructiveShell_AskWhenNoRule()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.AcceptEdits,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson(@"{""command"":""rm -rf /""}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Ask,
            "AcceptEdits routes destructive shell through EvaluateRules → Ask when no matching rule");
    }

    // Mode × tool-category decision matrix (behavior lock)

    [Theory]
    [InlineData(PermissionMode.Default, "Read", PermissionDecision.Allow)]
    [InlineData(PermissionMode.Default, "Write", PermissionDecision.Ask)]
    [InlineData(PermissionMode.Plan, "Read", PermissionDecision.Allow)]
    [InlineData(PermissionMode.Plan, "Write", PermissionDecision.Deny)]
    [InlineData(PermissionMode.BypassPermissions, "Write", PermissionDecision.Allow)]
    [InlineData(PermissionMode.AcceptEdits, "Write", PermissionDecision.Allow)]
    [InlineData(PermissionMode.DontAsk, "Write", PermissionDecision.Deny)]
    [InlineData(PermissionMode.GoalAuto, "Write", PermissionDecision.Allow)]
    [InlineData(PermissionMode.Team, "Write", PermissionDecision.Allow)]
    public async Task CheckAsync_ModeToolCategoryMatrix_WriteTool(
        PermissionMode mode, string toolName, PermissionDecision expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var workDir = Path.Combine(Path.GetTempPath(), "matrix_test");
        var ctx = new ToolPermissionContext { Mode = mode, WorkingDirectory = workDir };
        var input = ParseJson(@"{""file_path"":""file.txt"",""content"":""x""}");

        var result = await _sut.CheckAsync(toolName, input, ctx, ct);

        result.Decision.Should().Be(expected,
            $"{mode} mode should {expected} Write tool inside working directory");
    }

    [Theory]
    [InlineData(PermissionMode.Default, "git status", PermissionDecision.Allow)]
    [InlineData(PermissionMode.Plan, "git status", PermissionDecision.Allow)]
    [InlineData(PermissionMode.Plan, "dotnet build", PermissionDecision.Deny)]
    [InlineData(PermissionMode.GoalAuto, "dotnet build", PermissionDecision.Allow)]
    [InlineData(PermissionMode.Team, "dotnet build", PermissionDecision.Allow)]
    public async Task CheckAsync_ModeToolCategoryMatrix_ShellCommand(
        PermissionMode mode, string command, PermissionDecision expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = mode,
            WorkingDirectory = Path.GetTempPath(),
        };
        var input = ParseJson($@"{{""command"":""{command}""}}");

        var result = await _sut.CheckAsync("Bash", input, ctx, ct);

        result.Decision.Should().Be(expected,
            $"{mode} mode should {expected} for Bash '{command}'");
    }

    // SessionAllowlist interaction

    [Fact]
    public async Task CheckAsync_DefaultMode_ReadOnlyTool_IgnoresSessionAllowlist()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext
        {
            Mode = PermissionMode.Default,
            WorkingDirectory = Path.GetTempPath(),
            SessionAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Read" },
        };
        var input = ParseJson(@"{""path"":""file.txt""}");

        var result = await _sut.CheckAsync("Read", input, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow,
            "read-only tools are always allowed regardless of allowlist");
    }
}
