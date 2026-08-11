using OneCode.App.Tui;
using NSubstitute;
using Terminal.Gui.App;

namespace OneCode.Tests;

/// <summary>
/// Rapid Tab must keep the chat mode banner aligned with the live title-bar mode.
/// Stacking snapshot banners makes the message area appear one step behind.
/// </summary>
public sealed class ChatTranscriptViewModeBannerTests
{
    [Fact]
    public void UpdateModeBanner_RapidCycle_ReplacesTrailingBanner()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan));
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Team));
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Goal));

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("GOAL");
        text.Should().NotContain("PLAN");
        text.Should().NotContain("TEAM");
        // Banner is blank line + content line — only one such pair after rapid cycle.
        view.MessageView.RenderedLines.Count(l => l.Contains("GOAL")).Should().Be(1);
    }

    [Fact]
    public void UpdateModeBanner_AfterUserMessage_AppendsNewBanner()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan));
        view.AddUserMessageDirect("hello");
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Team));

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("PLAN");
        text.Should().Contain("TEAM");
        text.Should().Contain("hello");
    }

    [Fact]
    public void UpdateModeBanner_DuringStreaming_ReplacesBannerAbovePreview()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("streaming…");
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan));
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Team));
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("TEAM");
        text.Should().NotContain("PLAN");
        text.Should().Contain("streaming");
    }

    private static IApplication CreateImmediateApp()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return app;
    }
}
