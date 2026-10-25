using System.Text.Json;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// BuildShellPrompt 危险命令提示守护：标题必须携带危险命令警示。
/// 标题使用真实工具名（"$ Bash 命令"）由 OneCodeToplevelApprovalTests 的转写集成断言覆盖。
/// </summary>
public sealed class BuildShellPromptTests
{
    [Fact]
    public void BuildShellPrompt_DestructiveCommand_WarnsInTitle()
    {
        var input = JsonDocument.Parse("""{"command":"rm -rf /"}""").RootElement.Clone();

        var (title, message) = OneCodeToplevel.BuildShellPrompt("Bash", input);

        title.Should().Contain("危险命令");
        message.Should().Contain("危险");
    }
}
