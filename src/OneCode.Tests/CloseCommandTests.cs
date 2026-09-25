using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using NSubstitute;

namespace OneCode.Tests;

public sealed class CloseCommandTests
{
    private static Conversation ForegroundWithId(string id) => new()
    {
        Id = new SessionId(id),
        Name = "current",
        WorkingDirectory = @"e:\repo",
    };

    [Fact]
    public async Task ExecuteAsync_NoArguments_ClosesForegroundSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ISessionManager>();
        var foreground = ForegroundWithId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        manager.ForegroundConversation.Returns(foreground);
        manager.CloseAsync(ct).Returns(Task.CompletedTask);

        var command = new CloseCommand(manager);

        var result = await command.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await manager.Received(1).CloseAsync(ct);
    }

    [Fact]
    public async Task ExecuteAsync_NoArguments_NoActiveSession_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ISessionManager>();
        manager.ForegroundConversation.Returns((Conversation?)null);

        var command = new CloseCommand(manager);

        var result = await command.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("No active session");
    }

    [Fact]
    public async Task ExecuteAsync_WithForegroundId_ClosesForegroundNotBackground()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ISessionManager>();
        var foreground = ForegroundWithId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        manager.ForegroundConversation.Returns(foreground);
        manager.CloseAsync(ct).Returns(Task.CompletedTask);

        var command = new CloseCommand(manager);

        var result = await command.ExecuteAsync(["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        await manager.Received(1).CloseAsync(ct);
        await manager.DidNotReceive().CloseBackgroundSessionAsync(Arg.Any<string>(), ct);
    }

    [Fact]
    public async Task ExecuteAsync_WithBackgroundId_ClosesBackgroundSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ISessionManager>();
        var foreground = ForegroundWithId("cccccccccccccccccccccccccccccccc");
        manager.ForegroundConversation.Returns(foreground);
        manager.CloseBackgroundSessionAsync("dddddddddddddddddddddddddddddddd", ct)
            .Returns(true);

        var command = new CloseCommand(manager);

        var result = await command.ExecuteAsync(["dddddddddddddddddddddddddddddddd"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("dddddddddddddddddddddddddddddddd");
        await manager.DidNotReceive().CloseAsync(ct);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownId_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ISessionManager>();
        manager.ForegroundConversation.Returns((Conversation?)null);
        manager.CloseBackgroundSessionAsync("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", ct)
            .Returns(false);

        var command = new CloseCommand(manager);

        var result = await command.ExecuteAsync(["eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("not found");
    }
}
