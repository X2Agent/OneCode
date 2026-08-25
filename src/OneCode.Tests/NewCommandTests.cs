using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using NSubstitute;

namespace OneCode.Tests;

public sealed class NewCommandTests
{
    [Fact]
    public async Task ExecuteAsync_WithName_PassesNameAndInheritsCwd()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ISessionManager>();
        var current = new Conversation { Name = "old", WorkingDirectory = @"e:\repo" };
        manager.ForegroundConversation.Returns(current);

        var created = new Conversation { Name = "my task" };
        ConversationOptions? received = null;
        manager.BackgroundCurrentAndCreateNewAsync(
                Arg.Do<ConversationOptions>(o => received = o), ct)
            .Returns(created);

        var command = new NewCommand(manager);

        var result = await command.ExecuteAsync(["my", "task"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("my task");
        received.Should().NotBeNull();
        received!.Name.Should().Be("my task");
        received.WorkingDirectory.Should().Be(@"e:\repo");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutName_CreatesUnnamedSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ISessionManager>();
        manager.ForegroundConversation.Returns((Conversation?)null);

        var created = new Conversation { Name = "conversation-20260823" };
        ConversationOptions? received = null;
        manager.BackgroundCurrentAndCreateNewAsync(
                Arg.Do<ConversationOptions>(o => received = o), ct)
            .Returns(created);

        var command = new NewCommand(manager);

        var result = await command.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("conversation-20260823");
        received.Should().NotBeNull();
        received!.Name.Should().BeNull();
        received.WorkingDirectory.Should().Be(Directory.GetCurrentDirectory());
    }
}
