using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Core.Permissions;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// R2 行为契约：自动批准回调与执行期权限检查必须来自同一政策入口。
/// </summary>
/// <remarks>
/// 修复前两条路径各自计算：执行期用 <see cref="IPermissionChecker.CheckAsync"/>（Auto 模式含 YOLO），
/// 自动批准用 <see cref="PermissionProfiles.Check"/>（无 YOLO）。同一工具可能被一条路径允许、
/// 另一条路径拒绝，于是「需不需要问人」与「问了之后是否自动放行」给出矛盾答案。
/// </remarks>
public sealed class AutoApprovalRulesFactoryTests
{
    [Fact]
    public async Task AutoApprovalRule_DelegatesToTheSameCheckerAsExecution()
    {
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Allow);

        var rules = AutoApprovalRulesFactory.Create(
            PermissionMode.Default, "C:\\work", null, null, checker);

        var approved = await EvaluateToolRulesAsync(rules, "Write");

        approved.Should().BeTrue("the checker allowed the call");
        await checker.Received(1).CheckAsync(
            "Write",
            Arg.Any<JsonElement>(),
            Arg.Is<ToolPermissionContext>(c => c.Mode == PermissionMode.Default && c.WorkingDirectory == "C:\\work"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 反证：checker 判定非 Allow 时不得自动批准。
    /// 这条用例在「自动批准改为无条件 true」或「忽略 checker 结果」时失败。
    /// </summary>
    [Fact]
    public async Task AutoApprovalRule_CheckerDoesNotAllow_ReturnsFalse()
    {
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Ask("needs a human"));

        var rules = AutoApprovalRulesFactory.Create(
            PermissionMode.Default, "C:\\work", null, null, checker);

        var approved = await EvaluateToolRulesAsync(rules, "Write");

        approved.Should().BeFalse("Ask must not be silently converted into auto-approval");
    }

    /// <summary>
    /// 反证：Deny 同样不得自动批准，否则执行前硬闸会被自动批准路径绕过。
    /// </summary>
    [Fact]
    public async Task AutoApprovalRule_CheckerDenies_ReturnsFalse()
    {
        var checker = Substitute.For<IPermissionChecker>();
        checker.CheckAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(PermissionCheckResult.Deny("blocked"));

        var rules = AutoApprovalRulesFactory.Create(
            PermissionMode.Default, "C:\\work", null, null, checker);

        (await EvaluateToolRulesAsync(rules, "Write")).Should().BeFalse();
    }

    /// <summary>
    /// 无 checker 的回退路径仍可用（非交互路径与测试），且只放行 Allow。
    /// </summary>
    [Fact]
    public async Task AutoApprovalRule_WithoutChecker_UsesDeterministicProfiles()
    {
        var rules = AutoApprovalRulesFactory.Create(
            PermissionMode.BypassPermissions, "C:\\work", null, null);

        (await EvaluateToolRulesAsync(rules, "Write")).Should().BeTrue(
            "BypassPermissions allows everything through the profile table");
    }

    /// <summary>
    /// 执行规则列表，跳过 MAF 只读技能规则（它只对技能工具名生效）。
    /// 返回产品规则是否放行该工具。
    /// </summary>
    private static async Task<bool> EvaluateToolRulesAsync(
        List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> rules,
        string toolName)
    {
        var context = CreateRuleContext(toolName);
        foreach (var rule in rules)
        {
            if (await rule(context))
                return true;
        }

        return false;
    }

    private static ToolAutoApprovalRuleContext CreateRuleContext(string toolName)
    {
        var functionCall = new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>());
        return new ToolAutoApprovalRuleContext(
            functionCall,
            agent: Substitute.For<AIAgent>(),
            session: null,
            requestMessages: [],
            agentRunOptions: null);
    }
}
