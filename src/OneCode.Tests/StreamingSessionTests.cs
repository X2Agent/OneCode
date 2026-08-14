using OneCode.App.Query;
using OneCode.Core.Build;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="StreamingSession"/> — the ADR 0006 acceptance requires the
/// event-digestion logic (sequence, CallId dedup, token accumulation, suggestion extraction,
/// turn boundaries) to be verifiable without the full ChatService pipeline.
/// </summary>
public sealed class StreamingSessionTests
{
    private static StreamingSession CreateSut(
        bool includeNextPrompt = false,
        Action<string>? autoActivateTool = null,
        string agentRunId = "run-test")
        => new(agentRunId, includeNextPrompt, NullLogger.Instance, autoActivateTool);

    private static List<QueryEvent> DigestAll(StreamingSession sut, params object[] events)
        => events.SelectMany(sut.Digest).ToList();

    [Fact]
    public void Digest_ToolCallAndResult_EmitsStartThenDone_AndSealsBatch()
    {
        var sut = CreateSut();
        var fcc = new FunctionCallContent("call_1", "Read", new Dictionary<string, object?> { ["path"] = "test.txt" });
        var frc = new FunctionResultContent("call_1", "file content");

        var events = DigestAll(sut, new AgentResponseUpdate { Contents = { fcc, frc } });

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ToolStartEvent>()
            .Which.ToolName.Should().Be("Read");
        events[1].Should().BeOfType<ToolDoneEvent>()
            .Which.ToolName.Should().Be("Read", "result name resolves via toolNamesByCallId");

        sut.ToolBatchCollector.CompletedBatches.Should().HaveCount(1);
        sut.ToolBatchCollector.CompletedBatches[0].Calls.Single().CallId.Should().Be("call_1");
        sut.ToolBatchCollector.CompletedBatches[0].Results.Single().CallId.Should().Be("call_1");
        sut.ToolBatchCollector.HasOpenBatch.Should().BeFalse();
    }

    [Fact]
    public void Digest_ReplayedCallIdAcrossUpdates_DeduplicatesStartAndDone()
    {
        var sut = CreateSut();
        var fcc = new FunctionCallContent("call_dup", "Read", null);
        var frc = new FunctionResultContent("call_dup", "ok");
        var first = new AgentResponseUpdate { Contents = { fcc, frc } };

        // MAF replays the same call+result across a turn-boundary update
        var events = DigestAll(sut, first, new AgentResponseUpdate { Contents = { fcc, frc } });

        events.OfType<ToolStartEvent>().Should().HaveCount(1, "duplicate tool call with same CallId must be deduplicated");
        events.OfType<ToolDoneEvent>().Should().HaveCount(1, "duplicate tool result with same CallId must be deduplicated");
        sut.ToolBatchCollector.CompletedBatches.Should().HaveCount(1, "replay must not double-persist");
    }

    [Fact]
    public void Digest_UsageUpdates_ReplaceTotals_FinalUsageReflectsLatest()
    {
        var sut = CreateSut();
        var first = new AgentResponseUpdate
        {
            Contents = { new UsageContent(new UsageDetails { InputTokenCount = 500, OutputTokenCount = 200 }) }
        };
        var second = new AgentResponseUpdate
        {
            Contents =
            {
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 700,
                    OutputTokenCount = 300,
                    CachedInputTokenCount = 40,
                    AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_creation_input_tokens"] = 7 },
                }),
                new TextContent("done"),
            }
        };

        var events = DigestAll(sut, first, second);

        // Totals are replaced (not summed) — mirrors the runner's cumulative usage semantics.
        events.OfType<UsageUpdateEvent>().Should().HaveCount(2);
        sut.TotalInputTokens.Should().Be(700);
        sut.FinalUsage.InputTokens.Should().Be(700);
        sut.FinalUsage.OutputTokens.Should().Be(300);
        sut.FinalUsage.CacheReadTokens.Should().Be(40);
        sut.FinalUsage.CacheWriteTokens.Should().Be(7);
    }

    [Fact]
    public void Digest_TextAfterToolResult_StartsNewTurn_ContinuationKeepsSameTurn()
    {
        var sut = CreateSut();

        var events = DigestAll(
            sut,
            new AgentResponseUpdate { Contents = { new FunctionCallContent("call_t1", "Read", null) } },
            new AgentResponseUpdate { Contents = { new FunctionResultContent("call_t1", "ok") } },
            new AgentResponseUpdate { Contents = { new TextContent("first turn") } },
            new AgentResponseUpdate { Contents = { new TextContent(" still turn 1") } });

        events.OfType<TurnStartedEvent>().Should().ContainSingle()
            .Which.TurnNumber.Should().Be(1, "continuation text after the first text stays in the same turn");
        sut.TurnCount.Should().Be(1);
        sut.FinalText.Should().Be("first turn still turn 1");

        var secondTurn = DigestAll(
            sut,
            new AgentResponseUpdate { Contents = { new FunctionCallContent("call_t2", "Read", null) } },
            new AgentResponseUpdate { Contents = { new FunctionResultContent("call_t2", "ok") } },
            new AgentResponseUpdate { Contents = { new TextContent("second turn") } });

        secondTurn.OfType<TurnStartedEvent>().Should().ContainSingle().Which.TurnNumber.Should().Be(2);
        sut.TurnCount.Should().Be(2);
    }

    [Fact]
    public void Digest_ThinkingContent_EmitsThinkingDelta_WithoutAffectingTurns()
    {
        var sut = CreateSut();

        var events = DigestAll(sut, new AgentResponseUpdate
        {
            Contents = { new TextReasoningContent("pondering...") }
        });

        events.Should().ContainSingle().Which.Should().BeOfType<ThinkingDeltaEvent>()
            .Which.Text.Should().Be("pondering...");
        sut.TurnCount.Should().Be(0, "thinking content alone must not start a turn");
        sut.CompleteTurnIfStarted().Should().BeNull();
    }

    [Fact]
    public void Digest_NextPromptTag_SplitsAnswerFromSuggestion()
    {
        var sut = CreateSut(includeNextPrompt: true);

        var events = DigestAll(sut, new AgentResponseUpdate
        {
            Contents = { new TextContent("The answer.<onecode-next-prompt>Want tests next?</onecode-next-prompt>") }
        });

        events.OfType<TextDeltaEvent>().Should().ContainSingle()
            .Which.Text.Should().Be("The answer.", "suggestion is stripped from the visible stream");
        events.OfType<SuggestionsEvent>().Should().ContainSingle()
            .Which.Items.Should().Equal("Want tests next?");
        sut.FinalText.Should().Be("The answer.", "suggestion must not leak into the persisted transcript");
        sut.FlushTrailingText().Should().BeEmpty();
    }

    [Fact]
    public void FlushTrailingText_IncompleteTagEmittedAsText_NoContentLost()
    {
        var sut = CreateSut(includeNextPrompt: true);

        var events = DigestAll(sut, new AgentResponseUpdate
        {
            Contents = { new TextContent("End<onecode-next-pro") }
        });

        events.OfType<TextDeltaEvent>().Should().ContainSingle().Which.Text.Should().Be("End");
        var flushed = sut.FlushTrailingText().ToList();
        flushed.Should().ContainSingle().Which.Should().BeOfType<TextDeltaEvent>()
            .Which.Text.Should().Be("<onecode-next-pro");
        sut.FinalText.Should().Be("End<onecode-next-pro", "an interrupted response loses no content");
    }

    [Fact]
    public void Digest_ApprovalAndBuildRunEvents_PassThroughUnchanged()
    {
        var sut = CreateSut();
        var approval = new ApprovalRequestEvent("req-1", "Write", "path=src/x.cs");
        var buildState = new BuildRunStateEvent(new BuildRunId("br-1"), BuildRunState.Implementing, 3, []);

        var events = DigestAll(sut, approval, buildState);

        events.Should().HaveCount(2);
        events[0].Should().Be(approval, "approval requests pass straight through to the TUI");
        events[1].Should().Be(buildState, "BuildRun state projections pass through untouched");
    }

    [Fact]
    public void Digest_FunctionCall_InvokesAutoActivateCallback_EvenOnReplay()
    {
        var activated = new List<string>();
        var sut = CreateSut(autoActivateTool: activated.Add);
        var fcc = new FunctionCallContent("call_h1", "Hallucinated", null);

        DigestAll(sut,
            new AgentResponseUpdate { Contents = { fcc } },
            new AgentResponseUpdate { Contents = { fcc } });

        // Activation runs before dedup (idempotent TryActivate) — matches pre-refactor behavior.
        activated.Should().Equal("Hallucinated", "Hallucinated");
    }

    [Fact]
    public void CompleteTurnIfStarted_ReturnsEventOnlyWhenTextWasSeen()
    {
        var sut = CreateSut();
        sut.CompleteTurnIfStarted().Should().BeNull();

        DigestAll(sut, new AgentResponseUpdate { Contents = { new TextContent("hi") } });

        var completed = sut.CompleteTurnIfStarted();
        completed.Should().NotBeNull();
        completed!.TurnNumber.Should().Be(1);
        completed.HadToolCalls.Should().BeFalse();
    }
}
