using OneCode.App.Commands;
using OneCode.Core.Commands;
using NSubstitute;

namespace OneCode.Tests;

public sealed class BranchCommandTests
{
    [Fact]
    public async Task ExecuteAsync_WithoutArguments_ReturnsBranchList()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Is<string[]>(a => a.SequenceEqual(new[] { "branch" })), ct)
            .Returns(new GitCommandResult(true, "* main\n  feature", string.Empty));

        var command = new BranchCommand(gitHelper);

        var result = await command.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("Git branches:");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCheckoutFails_CreatesBranch()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Is<string[]>(a => a.SequenceEqual(new[] { "switch", "feature/test" })), ct)
            .Returns(new GitCommandResult(false, string.Empty, "pathspec did not match"));
        gitHelper.RunAsync(Arg.Is<string[]>(a => a.SequenceEqual(new[] { "switch", "-c", "feature/test" })), ct)
            .Returns(new GitCommandResult(true, "Switched to a new branch 'feature/test'", string.Empty));

        var command = new BranchCommand(gitHelper);

        var result = await command.ExecuteAsync(["feature/test"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("Created and switched to branch: feature/test");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCheckoutAndCreateFail_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(
                new GitCommandResult(false, string.Empty, "checkout failed"),
                new GitCommandResult(false, string.Empty, "branch create failed"));

        var command = new BranchCommand(gitHelper);

        var result = await command.ExecuteAsync(["feature/test"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("Failed to switch or create branch 'feature/test'");
    }
}
