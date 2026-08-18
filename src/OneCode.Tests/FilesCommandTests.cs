using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// /files 回归测试（B10）：命令实际列出会话中「涉及」的文件（含只读工具的路径），
/// 文案用 referenced 而非 changed/modified，避免与只读读取混淆。
/// </summary>
public sealed class FilesCommandTests
{
    [Fact]
    public async Task List_IncludesReadAndWriteTools_PrefixesReferenced()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = new Conversation();
        conv.Messages.Add(new AssistantMessage(
            "a1",
            new ContentBlock[]
            {
                new ToolUseBlock("t1", "Read", "{\"filePath\":\"/proj/a.cs\"}"),
                new ToolUseBlock("t2", "Write", "{\"filePath\":\"/proj/b.cs\"}"),
            },
            DateTimeOffset.UtcNow));

        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(conv);

        var sut = new FilesCommand(sessionManager);
        var result = await sut.ExecuteAsync([], ct);

        var text = result.Should().BeOfType<CommandResult.TextResult>().Subject.Value;
        text.Should().Contain("Files referenced (2):");
        text.Should().Contain("/proj/a.cs");
        text.Should().Contain("/proj/b.cs");
    }

    [Fact]
    public async Task List_NoTools_ShowsEmptyMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = new Conversation();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(conv);

        var sut = new FilesCommand(sessionManager);
        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("No files referenced in this conversation.");
    }
}