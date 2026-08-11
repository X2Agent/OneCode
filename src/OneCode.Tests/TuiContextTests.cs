using OneCode.App.Tui;
using OneCode.Core.Models;

namespace OneCode.Tests;

public sealed class TuiContextTests
{
    [Fact]
    public void ForwardingProperties_PreserveGroupedDependencies()
    {
        Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TuiEvent>> streamQuery =
            (_, _, _) => AsyncEnumerable.Empty<TuiEvent>();
        Func<CancellationToken, Task> createSession = _ => Task.CompletedTask;
        Func<string, CancellationToken, Task<string?>> executeCommand =
            (_, _) => Task.FromResult<string?>(null);
        Func<string?> getSessionName = () => "session";
        Func<bool> getShowThinking = () => true;
        Action<TuiEvent> emitEvent = _ => { };
        var modeController = new WorkingModeController();
        var modelCatalog = new ModelCatalogStore();
        var slashCommands = new[] { new SlashCommandEntry("help", "Show help") };
        using var cancellation = new CancellationTokenSource();

        var context = new TuiContext(
            new TuiQueryServices(
                streamQuery,
                createSession,
                executeCommand),
            new TuiSessionServices(GetSessionName: getSessionName),
            new TuiDiagnosticServices(),
            new TuiRuntimeServices(
                Model: "test-model",
                ModelCatalog: modelCatalog,
                ModeController: modeController,
                GetShowThinking: getShowThinking,
                EmitEvent: emitEvent),
            new TuiLaunchOptions(
                Version: "1.2.3",
                ExternalCancellation: cancellation.Token,
                SlashCommands: slashCommands,
                SshHost: "build-host",
                InitialPrompt: "inspect"));

        context.StreamQuery.Should().BeSameAs(streamQuery);
        context.CreateSession.Should().BeSameAs(createSession);
        context.ExecuteCommand.Should().BeSameAs(executeCommand);
        context.GetSessionName.Should().BeSameAs(getSessionName);
        context.Model.Should().Be("test-model");
        context.ModelCatalog.Should().BeSameAs(modelCatalog);
        context.ModeController.Should().BeSameAs(modeController);
        context.GetShowThinking.Should().BeSameAs(getShowThinking);
        context.EmitEvent.Should().BeSameAs(emitEvent);
        context.Version.Should().Be("1.2.3");
        context.ExternalCancellation.Should().Be(cancellation.Token);
        context.SlashCommands.Should().BeSameAs(slashCommands);
        context.SshHost.Should().Be("build-host");
        context.InitialPrompt.Should().Be("inspect");
    }
}
