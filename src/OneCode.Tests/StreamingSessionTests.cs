using OneCode.App.Query;
using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="StreamingSession"/> — the acceptance requires the
/// event-digestion logic (sequence, CallId dedup, token accumulation, suggestion extraction,
/// turn boundaries) to be verifiable without the full query pipeline.
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
    public void Digest_ToolDone_CarriesToolInputForTargetDisplay()
    {
        // 完成行必须带参数 JSON，否则 TUI 完成行丢失搜索词/路径等目标段，
        // 同类调用（如连续 WebSearch）在转录中无法区分。
        var sut = CreateSut();
        var fcc = new FunctionCallContent("call_1", "WebSearch", new Dictionary<string, object?> { ["query"] = "波场 9月" });
        var frc = new FunctionResultContent("call_1", "{}");

        var events = DigestAll(sut, new AgentResponseUpdate { Contents = { fcc, frc } });

        var done = events.OfType<ToolDoneEvent>().Single();
        done.ToolInput.Should().NotBeNull();
        done.ToolInput.Should().Contain("波场 9月");
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

        // Totals are replaced (not summed) within the same turn — mirrors the runner's cumulative usage semantics.
        events.OfType<UsageUpdateEvent>().Should().HaveCount(2);
        sut.TotalInputTokens.Should().Be(700);
        sut.FinalUsage.InputTokens.Should().Be(700);
        sut.FinalUsage.OutputTokens.Should().Be(300);
        sut.FinalUsage.CacheReadTokens.Should().Be(40);
        sut.FinalUsage.CacheWriteTokens.Should().Be(7);
    }

    [Fact]
    public void Digest_MultiTurnToolLoop_AccumulatesUsageAcrossTurns_AndDetectsUnwrappedStringErrors()
    {
        var sut = CreateSut();

        var events = DigestAll(
            sut,
            new AgentResponseUpdate
            {
                Contents =
                {
                    new FunctionCallContent("call_1", "WebSearch", new Dictionary<string, object?> { ["query"] = "october news" }),
                    new UsageContent(new UsageDetails { InputTokenCount = 20_000, OutputTokenCount = 500, CachedInputTokenCount = 15_000 }),
                }
            },
            new AgentResponseUpdate
            {
                Contents = { new FunctionResultContent("call_1", """{"Content":"WebSearch error: all providers failed","IsError":true}""") }
            },
            new AgentResponseUpdate
            {
                Contents =
                {
                    new TextContent("Summary ready."),
                    new UsageContent(new UsageDetails { InputTokenCount = 22_000, OutputTokenCount = 60, CachedInputTokenCount = 18_000 }),
                }
            });

        var toolDone = events.OfType<ToolDoneEvent>().Single();
        toolDone.IsError.Should().BeTrue("unwrapped string JSON envelope with IsError=true must surface as error in ToolDoneEvent");

        sut.FinalUsage.InputTokens.Should().Be(42_000);
        sut.FinalUsage.OutputTokens.Should().Be(560, "earlier turn output tokens must not be overwritten by the final turn");
        sut.FinalUsage.CacheReadTokens.Should().Be(33_000);
        sut.LastTurnInputTokens.Should().Be(22_000, "LastTurnInputTokens tracks the latest turn's context window watermark");
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
        sut.LastTurnText.Should().Be("first turn still turn 1");

        var secondTurn = DigestAll(
            sut,
            new AgentResponseUpdate { Contents = { new FunctionCallContent("call_t2", "Read", null) } },
            new AgentResponseUpdate { Contents = { new FunctionResultContent("call_t2", "ok") } },
            new AgentResponseUpdate { Contents = { new TextContent("    \n  second turn") } });

        secondTurn.OfType<TurnStartedEvent>().Should().ContainSingle().Which.TurnNumber.Should().Be(2);
        secondTurn.OfType<TextDeltaEvent>().Should().ContainSingle().Which.Text.Should().Be("second turn",
            "leading whitespace emitted after reasoning at turn start must be stripped");
        sut.TurnCount.Should().Be(2);
        sut.FinalText.Should().Be("first turn still turn 1\n\nsecond turn",
            "turns must be separated by paragraph break instead of glued together");
        sut.LastTurnText.Should().Be("second turn",
            "LastTurnText must contain only the final turn's text without earlier turn narration");
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
    public void Digest_CumulativeReasoningUpdates_EmitOnlyIncrements()
    {
        // 部分提供方/MAF 重放会把"到目前为止的完整 reasoning"随每个 update 重发；
        // TUI 对 ThinkingDeltaEvent 逐条追加，不去重就会呈现前缀式增长
        //（"Now" → "Now I have a good" → …）。
        var sut = CreateSut();

        var events = DigestAll(sut,
            new AgentResponseUpdate { Contents = { new TextReasoningContent("Now") } },
            new AgentResponseUpdate { Contents = { new TextReasoningContent("Now I have a good") } },
            new AgentResponseUpdate { Contents = { new TextReasoningContent("Now I have a good picture. Let me check") } });

        var deltas = events.OfType<ThinkingDeltaEvent>().Select(e => e.Text).ToList();
        string.Concat(deltas).Should().Be("Now I have a good picture. Let me check",
            "cumulative reasoning must collapse to true increments so the TUI assembles each character exactly once");
        deltas.Should().HaveCount(3, "one increment per update");
        sut.TurnCount.Should().Be(0, "reasoning dedup must not affect turn state");
    }

    [Fact]
    public void Digest_ReplayedReasoningAfterToolResult_DeduplicatesToSingleEmission()
    {
        // MAF turn-history 重放：turn 边界后原样重发上一轮的 reasoning/tool 内容。
        // tool 事件按 CallId 去重，reasoning 也必须同样去重。
        var sut = CreateSut();
        var reasoning = new TextReasoningContent("Now I have a good picture. Let me check the results.");
        var fcc = new FunctionCallContent("call_r1", "WebSearch", null);
        var frc = new FunctionResultContent("call_r1", "[]");
        var turn = new AgentResponseUpdate { Contents = { reasoning, fcc, frc } };

        DigestAll(sut, turn);
        var replayEvents = DigestAll(sut, turn);

        replayEvents.OfType<ThinkingDeltaEvent>().Should().BeEmpty("replayed reasoning must not be re-emitted");
        replayEvents.OfType<ToolStartEvent>().Should().BeEmpty("tool replay is deduped by CallId");
    }

    [Fact]
    public void Digest_ReplayedOlderReasoningPrefix_SkipsWithoutStateChange()
    {
        var sut = CreateSut();

        DigestAll(sut,
            new AgentResponseUpdate { Contents = { new TextReasoningContent("first part") } },
            new AgentResponseUpdate { Contents = { new TextReasoningContent("first part second part") } });
        var events = DigestAll(sut, new AgentResponseUpdate
        {
            Contents = { new TextReasoningContent("first part") }
        });

        events.OfType<ThinkingDeltaEvent>().Should().BeEmpty("an older-prefix replay is stale content");
    }

    [Fact]
    public void Digest_DisjointReasoningDeltas_PassThroughUnchanged()
    {
        // 真正的逐块 delta（互不重叠）必须原样透传，不能被前缀逻辑误伤。
        var sut = CreateSut();

        var events = DigestAll(sut,
            new AgentResponseUpdate { Contents = { new TextReasoningContent("chunk one ") } },
            new AgentResponseUpdate { Contents = { new TextReasoningContent("chunk two") } });

        events.OfType<ThinkingDeltaEvent>().Select(e => e.Text)
            .Should().Equal("chunk one ", "chunk two");
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
    public void Digest_ApprovalEvent_PassesThroughUnchanged()
    {
        // durableStateObserver → BuildRunStateEvent 直通透传退役——受控 attempt
        // 改发 OrchestrationEvent.BuildStateProjectionChanged，由 Digest 解信封为流内
        // BuildRunStateEvent（见 Digest_OrchestrationBuildProjection_IsUnwrappedToStreamContract）。
        var sut = CreateSut();
        var approval = new ApprovalRequestEvent("req-1", "Write", "path=src/x.cs");

        var events = DigestAll(sut, approval);

        events.Should().ContainSingle()
            .Which.Should().Be(approval, "approval requests pass straight through to the TUI");
    }

    [Fact]
    public void Digest_OrchestrationBuildProjection_IsUnwrappedToStreamContract()
    {
        // 受控 attempt 以 OrchestrationEvent.BuildStateProjectionChanged
        // 统一信封发射，Digest 为其解信封唯一投影点——流内契约仍是 BuildRunStateEvent。
        var sut = CreateSut();
        var run = new BuildRun
        {
            Id = new BuildRunId("br-1"),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Verifying,
            SequenceNumber = 3,
            Version = 4,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var events = DigestAll(sut, new OrchestrationEvent.BuildStateProjectionChanged(run));

        events.Should().ContainSingle().Which.Should().BeOfType<BuildRunStateEvent>()
            .Which.State.Should().Be(BuildRunState.Verifying);
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

    [Fact]
    public void Digest_SubagentProgressUpdate_ProjectsToSubagentProgressEvent()
    {
        // fork 经旁路通道写入的原始信封必须原样投影为流内事件，不被 MAF 分支吞掉。
        var sut = CreateSut();

        var events = DigestAll(sut, new SubagentProgressUpdate(
            "f1", "Explore", SubagentProgressPhase.Running,
            ToolCallCount: 5, TurnCount: 2, CurrentToolName: "WebSearch", ElapsedMs: 1234));

        var projected = events.Should().ContainSingle(e => e is SubagentProgressEvent)
            .Subject as SubagentProgressEvent;
        projected!.ForkId.Should().Be("f1");
        projected.AgentName.Should().Be("Explore");
        projected.Phase.Should().Be(SubagentProgressPhase.Running);
        projected.ToolCallCount.Should().Be(5);
        projected.TurnCount.Should().Be(2);
        projected.CurrentToolName.Should().Be("WebSearch");
        projected.ElapsedMs.Should().Be(1234);
    }

    [Fact]
    public void Digest_SubagentProgressPhaseTransition_PreservesPhaseDiscrimination()
    {
        // Started/Completed/Failed 是徽标配对计数的依据——Phase 在投影中不得丢失或折叠。
        var sut = CreateSut();

        var events = DigestAll(sut,
            new SubagentProgressUpdate("f1", "Explore", SubagentProgressPhase.Started),
            new SubagentProgressUpdate("f1", "Explore", SubagentProgressPhase.Completed, TurnCount: 3));

        events.Select(e => ((SubagentProgressEvent)e).Phase)
            .Should().Equal(SubagentProgressPhase.Started, SubagentProgressPhase.Completed);
    }
}
