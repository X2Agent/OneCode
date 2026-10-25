using OneCode.App.Services.Interaction;
using OneCode.Core.Permissions;

using OneCode.Tests.TestSupport.Tui;

using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// 审批选择器的交互契约：审批端口收到请求 → 内联选择器接管键盘 → 用户选择经
/// <c>ResponseSource</c> 回传成 <see cref="ApprovalDecision"/>。
///
/// 断言面是「回传给调用方的决策」而不是选择器内部字段：同一串按键在不同选项集合
/// （升级档开关、参数解析失败）下必须落到不同决策，这正是用户真正的安全边界。
/// </summary>
public sealed class OneCodeToplevelApprovalTests
{
    private const string ValidShellInput = """{"command":"dotnet build"}""";
    private const string MalformedInput = "{not-json";

    [Fact]
    public async Task ApprovalRequest_ValidShellInput_RendersPromptAndAllFourOptions()
    {
        var approval = new ApprovalInteractionRequest("req-1", "Bash", ValidShellInput);
        using var shell = TuiTestShell.Create();

        shell.OnUiThread<bool>(() => { shell.Toplevel.HandleInteraction(approval); return true; });
        await shell.WaitUntilAsync(() => shell.Shell.HasActiveInteractionSession(), "审批选择器接管键盘");

        shell.Shell.HasActiveInteractionSession().Should().BeTrue("审批未决时必须接管键盘");

        var text = shell.TranscriptText;
        text.Should().Contain("$ Bash 命令", "标题走 Bash 分支，说明工具输入真的被解析成了命令提示");
        text.Should().Contain("dotnet build");
        text.Should().Contain("允许执行 (仅本次)");
        text.Should().Contain("始终允许此工具 (不再提示)");
        text.Should().Contain("本次对话全部允许 (所有工具不再提示)");
        text.Should().Contain("拒绝");
    }

    [Fact]
    public async Task ApprovalRequest_EnterOnDefaultOption_ReturnsAllowOnce()
    {
        var approval = new ApprovalInteractionRequest("req-1", "Bash", ValidShellInput);
        using var shell = TuiTestShell.Create();

        shell.OnUiThread<bool>(() => { shell.Toplevel.HandleInteraction(approval); return true; });
        await shell.WaitUntilAsync(() => shell.Shell.HasActiveInteractionSession(), "审批选择器接管键盘");
        shell.TypeKey(Key.Enter);

        // Enter → 选择器确认 → 收尾经 Invoke 回到 UI 线程后才回传决策，
        // 所以先等界面补完这一拍，再断言回传值。
        await shell.WaitUntilAsync(() => approval.ResponseSource.Task.IsCompleted, "审批决策回传");

        var decision = await approval.ResponseSource.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        decision.Should().Be(ApprovalDecision.AllowOnce, "默认停在「仅本次」，不得升级成永久放行");
        shell.Shell.HasActiveInteractionSession().Should().BeFalse("决策回传后选择器必须收尾");
        // Enter 已被选择器消费（决策已回传），不会再落回聊天输入。
    }

    [Fact]
    public async Task ApprovalRequest_SelectSessionEscalation_ReturnsAllowAllAndAppliesRuntimeOverride()
    {
        var applied = false;
        var approval = new ApprovalInteractionRequest("req-1", "Bash", ValidShellInput);
        using var shell = TuiTestShell.Create();
        shell.Toplevel.ConfigureSessionPermissionEscalation(() => applied = true);

        shell.OnUiThread<bool>(() => { shell.Toplevel.HandleInteraction(approval); return true; });
        await shell.WaitUntilAsync(() => shell.Shell.HasActiveInteractionSession(), "审批选择器接管键盘");
        shell.TypeKey(Key.CursorDown);
        shell.TypeKey(Key.CursorDown);
        shell.TypeKey(Key.Enter);

        var decision = await approval.ResponseSource.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        decision.Should().Be(ApprovalDecision.AllowAllConversation);
        applied.Should().BeTrue("选升级档必须真的翻转会话权限覆盖，只回传字符串等于没生效");
        shell.TranscriptText.Should().Contain("已允许本次对话全部工具调用");
        shell.Shell.HasActiveInteractionSession().Should().BeFalse("决策回传后必须交还键盘");
    }

    [Fact]
    public async Task ApprovalRequest_EscDismiss_ReturnsDeny()
    {
        var approval = new ApprovalInteractionRequest("req-1", "Bash", ValidShellInput);
        using var shell = TuiTestShell.Create();

        shell.OnUiThread<bool>(() => { shell.Toplevel.HandleInteraction(approval); return true; });
        await shell.WaitUntilAsync(() => shell.Shell.HasActiveInteractionSession(), "审批选择器接管键盘");
        shell.TypeKey(Key.Esc);

        var decision = await approval.ResponseSource.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        decision.Should().Be(ApprovalDecision.Deny, "取消审批必须 fail-closed");
        shell.Shell.HasActiveInteractionSession().Should().BeFalse();
    }

    [Fact]
    public async Task ApprovalRequest_MalformedToolInput_OffersDenyOnlyAndCannotBeEscalated()
    {
        var approval = new ApprovalInteractionRequest("req-1", "Bash", MalformedInput);
        using var shell = TuiTestShell.Create();

        shell.OnUiThread<bool>(() => { shell.Toplevel.HandleInteraction(approval); return true; });
        await shell.WaitUntilAsync(() => shell.Shell.HasActiveInteractionSession(), "审批选择器接管键盘");

        shell.TranscriptText.Should().Contain("⚠ 参数无法解析");
        shell.TranscriptText.Should().Contain("拒绝");
        shell.TranscriptText.Should().NotContain("允许执行", "参数无法解析时不得给出任何放行档位");
        shell.TranscriptText.Should().NotContain("始终允许");

        shell.TypeKey(Key.CursorDown);
        shell.TypeKey(Key.CursorDown);
        shell.TypeKey(Key.Enter);

        var decision = await approval.ResponseSource.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        decision.Should().Be(ApprovalDecision.Deny, "只有拒绝档时任何按键序列都不能放行");
        shell.Shell.HasActiveInteractionSession().Should().BeFalse("决策回传后必须交还键盘");
    }
}
