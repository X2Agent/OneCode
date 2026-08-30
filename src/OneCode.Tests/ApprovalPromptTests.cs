using System.Text.Json;
using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// 审批提示构造回归守护：Shell 工具的提示标题必须使用真实工具名，
/// 与执行后对话列表里的工具行一致（旧实现硬编码 "Shell 命令"，与 "Bash" 对不上）。
/// </summary>
public sealed class ApprovalPromptTests
{
    [Fact]
    public void BuildShellPrompt_TitleUsesRealToolName()
    {
        var input = JsonDocument.Parse("""{"command":"dotnet build"}""").RootElement.Clone();

        var (title, message) = OneCodeToplevel.BuildShellPrompt("Bash", input);

        title.Should().Be("$ Bash 命令");
        message.Should().Contain("工具：Bash");
        message.Should().Contain("dotnet build");
    }

    [Fact]
    public void BuildShellPrompt_DestructiveCommand_WarnsInTitle()
    {
        var input = JsonDocument.Parse("""{"command":"rm -rf /"}""").RootElement.Clone();

        var (title, message) = OneCodeToplevel.BuildShellPrompt("Bash", input);

        title.Should().Contain("危险命令");
        message.Should().Contain("危险");
    }
}
