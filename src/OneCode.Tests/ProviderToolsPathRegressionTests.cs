using System.Text.Json;
using OneCode.Core.Permissions;
using OneCode.Core.Permissions.Yolo;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware.Invariants;

namespace OneCode.Tests;

/// <summary>
/// provider 注入工具的零回归锁：<c>todos_*</c> 与 <c>FileMemoryProvider</c> 的工具由 MAF 直接注入，
/// 注册为 <c>Risk = Safe</c> + <c>Category = PlanAllowed</c>，只读写 agent 自己的会话状态目录。
/// 它们必须落在 <see cref="ToolPathScopeKind.None"/>——否则工作记忆会被敏感路径/边界规则误拦。
/// </summary>
public sealed class ProviderToolsPathRegressionTests
{
    private const string WorkingDir = @"C:\work\repo";

    [Fact]
    public void Scope_ProviderTools_AreNone()
    {
        foreach (var name in HarnessProviderTools.Names)
            ToolPathScope.Classify(name).Should().Be(ToolPathScopeKind.None, $"{name} 只触碰会话状态");
    }

    [Fact]
    public async Task FileSystemInvariant_DoesNotInspectProviderToolPaths()
    {
        var invariant = new FileSystemInvariant(WorkingDir);
        var parameters = new Dictionary<string, object?>();

        foreach (var name in HarnessProviderTools.FileMemoryToolNames)
        {
            var result = await invariant.CheckAsync(
                new InvariantCheckRequest(
                    name,
                    parameters,
                    ToolTargetPathResult.Resolved([@"C:\outside\.env"])),
                TestContext.Current.CancellationToken);

            result.Allowed.Should().BeTrue($"{name} 的记忆文件不在工作区，但也不属于敏感路径检查范围");
        }
    }

    [Fact]
    public async Task PermissionChecker_DoesNotDenyProviderToolPaths()
    {
        // 生产路径上中间件仍会为 provider 工具填 TargetPath；边界校验必须按分类跳过。
        var ruleStore = new YoloRuleStore(logger: null);
        ruleStore.ClearRules();
        var checker = new PermissionChecker(
            new YoloClassifier(ruleStore, new ToolMetadataRegistry(), logger: null));

        var workspace = Path.Combine(Path.GetTempPath(), "oc-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var context = new ToolPermissionContext
            {
                Mode = PermissionMode.Default,
                WorkingDirectory = workspace,
                TargetPath = ToolTargetPathResult.Resolved(
                    [Path.Combine(Path.GetTempPath(), "agent-file-memory", "notes.md")]),
            };

            using var doc = JsonDocument.Parse(@"{}");
            var result = await checker.CheckAsync(
                HarnessProviderTools.TodoToolNames.First(),
                doc.RootElement.Clone(),
                context,
                TestContext.Current.CancellationToken);

            result.Decision.Should().NotBe(PermissionDecision.Deny);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
