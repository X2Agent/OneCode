using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using NSubstitute;

namespace OneCode.Tests;

public sealed class SessionCommandTests
{
    private static (SessionCommand Command, ISessionManager Manager) CreateSut()
    {
        var manager = Substitute.For<ISessionManager>();
        var command = new SessionCommand(manager, new NewCommand(manager), new CloseCommand(manager));
        return (command, manager);
    }

    [Fact]
    public async Task ExecuteAsync_NewSubcommand_ForwardsMultiWordName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (command, manager) = CreateSut();
        manager.ForegroundConversation.Returns((Conversation?)null);
        var created = new Conversation { Name = "my session" };
        ConversationOptions? received = null;
        manager.BackgroundCurrentAndCreateNewAsync(
                Arg.Do<ConversationOptions>(o => received = o), ct)
            .Returns(created);

        var result = await command.ExecuteAsync(["new", "my", "session"], ct);

        // 多词名称经 args.Skip(1) + Join 后必须完整到达 SessionManager，不能被截断
        result.Should().BeOfType<CommandResult.TextResult>();
        received.Should().NotBeNull();
        received!.Name.Should().Be("my session");
    }

    [Fact]
    public async Task ExecuteAsync_InfoSubcommand_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (command, _) = CreateSut();

        var result = await command.ExecuteAsync(["info"], ct);

        // 废弃的 /session info 重定向已删除，不应再静默跳转到 /status
        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("Unknown session command");
    }
}
