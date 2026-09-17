using NSubstitute;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Exec;
using OneCode.Core.Tools;

namespace OneCode.Tests;

public sealed class ShellProviderSeamTests
{
    [Fact]
    public async Task BashTool_CanSwitchProviders_WithoutConsumerChanges()
    {
        var workingDirectory = Directory.GetCurrentDirectory();
        var workingDirectoryAccessor = Substitute.For<IWorkingDirectoryAccessor>();
        workingDirectoryAccessor.WorkingDirectory.Returns(workingDirectory);

        var sessionAccess = Substitute.For<ISessionConversationAccess>();
        var localProvider = new FakeShellProvider("local provider");
        var sandboxProvider = new FakeShellProvider("sandbox provider");

        var localTool = new BashTool(workingDirectoryAccessor, localProvider, sessionAccess);
        var sandboxTool = new BashTool(workingDirectoryAccessor, sandboxProvider, sessionAccess);

        var localResult = await localTool.ExecuteAsync("echo hello", ct: TestContext.Current.CancellationToken);
        var sandboxResult = await sandboxTool.ExecuteAsync("echo hello", ct: TestContext.Current.CancellationToken);

        localResult.Content.Should().Contain("local provider");
        sandboxResult.Content.Should().Contain("sandbox provider");
        localProvider.LastRequest.Should().NotBeNull();
        sandboxProvider.LastRequest.Should().NotBeNull();
        localProvider.LastRequest!.Command.Should().Be("echo hello");
        sandboxProvider.LastRequest!.Shell.Should().Be("bash");
    }

    private sealed class FakeShellProvider(string output) : IShellExecutor
    {
        public ShellExecutionRequest? LastRequest { get; private set; }

        public Task<ShellExecutionResult> ExecuteAsync(
            ShellExecutionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ShellExecutionResult(output, string.Empty, 0, false));
        }
    }
}