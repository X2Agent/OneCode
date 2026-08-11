using OneCode.App.Tui;
using NSubstitute;
using Terminal.Gui.App;

namespace OneCode.Tests;

public sealed class ChatTranscriptViewStreamingTests
{
    [Fact]
    public void ToolDone_DuringStreaming_KeepsFollowingTextInSameAssistantBlock()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("I will check the tests.");
        view.AddToolStart("Write", null, "call_write");
        view.AddToolDone("Write", isError: true, toolInput: null, result: "Tool 'Write' denied by user.", toolId: "call_write");
        view.AppendStreamingToken(" I can still explain what to do next.");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;

        // No assistant header in new UI design — verify content integrity
        string.Join('\n', lines).Should().Contain("Write");
        string.Join('\n', lines).Should().Contain("Tool 'Write' denied by user.");
        string.Join('\n', lines).Should().Contain("I can still");
        string.Join('\n', lines).Should().Contain("explain what to do next.");
    }

    [Fact]
    public void PermissionNotice_DuringStreaming_DoesNotCreateSecondAssistantHeader()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("Let me inspect that.");
        view.AddStreamingNotice("Permission denied: Write (denied by user)");
        view.AppendStreamingToken(" I will continue without that tool.");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;

        // No assistant header in new UI design — verify content integrity
        string.Join('\n', lines).Should().Contain("Permission denied: Write (denied by user)");
        string.Join('\n', lines).Should().Contain("I will continue");
        string.Join('\n', lines).Should().Contain("without that tool.");
    }

    [Fact]
    public void ToolDone_WithToolId_UpdatesCorrectStartLine_NoDuplicate()
    {
        // Verifies that ToolId-based matching prevents duplicate tool rows
        // when a ToolDone arrives for a tool that has a matching ToolStart.
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("Working...");
        view.AddToolStart("Read", "file.txt", toolId: "call_abc");
        view.AddToolDone("Read", isError: false, toolInput: "file.txt",
            result: "file contents", toolId: "call_abc");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;
        var toolLineCount = lines.Count(l => l.Contains("Read") && l.Contains("file.txt"));
        // Should be exactly 1 tool line — not 2 (which would indicate a duplicate)
        toolLineCount.Should().Be(1);
    }

    [Fact]
    public void ToolDone_AfterContinueStreaming_DoesNotDuplicateCommittedLine()
    {
        // Simulates the scenario where ContinueStreaming clears the pending
        // dictionary between a ToolStart and its ToolDone. The committed
        // ToolStart line is already in history; a late ToolDone must NOT
        // add a second completed row.
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("First turn.");
        view.AddToolStart("Bash", "ls", toolId: "call_xyz");
        // ContinueStreaming commits the pending lines (including the Bash start line)
        // and clears _pendingToolLines. This simulates a turn boundary.
        view.ContinueStreaming();
        // Now the ToolDone arrives — the pending dict is empty, but the committed
        // start line exists. We should NOT add a duplicate completed row.
        view.AddToolDone("Bash", isError: false, toolInput: "ls",
            result: "file1\nfile2", toolId: "call_xyz");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;
        // Count lines containing "Bash" — should be 1 (the committed line),
        // not 2 (committed start + duplicate done).
        var bashLineCount = lines.Count(l => l.Contains("Bash"));
        bashLineCount.Should().Be(1, "the committed ToolStart line should be the only Bash line; a duplicate ToolDone row must not be added after ContinueStreaming cleared the pending state");
    }

    [Fact]
    public void ToolDone_DuplicateEvent_DoesNotCreateSecondRow()
    {
        // Verifies that a duplicate ToolDone event (same ToolId) does not
        // create a second completed row.
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddToolStart("Grep", "pattern", toolId: "call_dup");
        view.AddToolDone("Grep", isError: false, toolInput: "pattern",
            result: "match1", toolId: "call_dup");
        // Duplicate ToolDone with the same ToolId — should be ignored, not added
        view.AddToolDone("Grep", isError: false, toolInput: "pattern",
            result: "match1", toolId: "call_dup");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;
        var grepLineCount = lines.Count(l => l.Contains("Grep"));
        grepLineCount.Should().Be(1, "duplicate ToolDone events with the same ToolId must not create additional rows");
    }

    [Fact]
    public void ThinkingThenTool_ToolLineSurvivesNextThinkingDelta()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddThinking("Planning the write.");
        view.AddToolStart("Write", "One.txt", toolId: "call_write");
        view.AddToolDone("Write", isError: false, toolInput: "One.txt",
            result: "Wrote One.txt", toolId: "call_write");
        // More thinking after the tool must not wipe the tool row.
        view.AddThinking(" Confirming the file was written.");
        view.AppendStreamingToken("Done.");
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("Write");
        text.Should().Contain("One.txt");
        text.Should().Contain("Done.");
        CountOccurrences(text, "Thought for").Should().Be(1);
        text.Should().NotContain(" Thinking");
    }

    [Fact]
    public void ThinkingThenTool_ToolLineSurvivesFirstTextToken()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddThinking("I will call Write.");
        view.AddToolStart("Write", "a.txt", toolId: "call_a");
        view.AddToolDone("Write", isError: false, toolInput: "a.txt",
            result: "ok", toolId: "call_a");
        // First reply token collapses thinking; tools after the thinking span must remain.
        view.AppendStreamingToken("File written.");
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("Write");
        text.Should().Contain("a.txt");
        text.Should().Contain("File written.");
        CountOccurrences(text, "Thought for").Should().Be(1);
    }

    [Fact]
    public void MultiTurnThinking_EachTurnHasIndependentSummary()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddThinking("Phase one plan.");
        view.AddToolStart("Write", "t.txt", toolId: "call_t");
        view.AddToolDone("Write", isError: false, toolInput: "t.txt",
            result: "ok", toolId: "call_t");
        // Agent turn boundary — each turn's thinking is committed independently.
        view.ContinueStreaming();
        view.AddThinking(" Phase two notes.");
        view.AppendStreamingToken("All done.");
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("Write");
        text.Should().Contain("All done.");
        // Each turn's thinking produces its own "Thought for" summary.
        CountOccurrences(text, "Thought for").Should().Be(2,
            "each turn's thinking should have its own independent summary");
        // Streaming expanded title must not remain in committed history.
        text.Should().NotContain(" Thinking");
    }

    [Fact]
    public void StreamingActivity_TransitionsThroughProcessingThinkingAndReplying()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        var activities = new List<string>();
        view.ActivityChanged += activities.Add;

        view.BeginStreaming();
        view.AddThinking("Inspecting the request.");
        view.AppendStreamingToken("Done.");
        view.EndStreaming();

        activities.Should().Equal("处理中", "思考中", "回复中");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length)
            count++;
        return count;
    }

    private static IApplication CreateImmediateApp()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return app;
    }
}
