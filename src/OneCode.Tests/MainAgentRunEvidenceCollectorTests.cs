using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.Core.Tools;

namespace OneCode.Tests;

public sealed class MainAgentRunEvidenceCollectorTests
{
    [Fact]
    public void Observe_CollectsUsageTurnsAndCompletedToolBatch()
    {
        var collector = new MainAgentRunEvidenceCollector("agent-run-1");
        collector.Observe(Update(new UsageContent(new UsageDetails
        {
            InputTokenCount = 120,
            OutputTokenCount = 35,
        }), new TextContent("first turn")));
        collector.Observe(Update(new FunctionCallContent(
            "call-1",
            "Read",
            new Dictionary<string, object?> { ["path"] = "a.txt" })));
        collector.Observe(Update(new FunctionResultContent("call-1", "contents")));
        collector.Observe(Update(new TextContent("second turn")));

        collector.InputTokens.Should().Be(120);
        collector.OutputTokens.Should().Be(35);
        collector.TurnCount.Should().Be(2);
        collector.CompletedToolBatches.Should().ContainSingle();
        collector.CompletedToolBatches[0].BatchId.Should().Be("agent-run-1:1");
        collector.CompletedToolBatches[0].Calls.Should().ContainSingle();
        collector.CompletedToolBatches[0].Results.Should().ContainSingle();
    }

    [Fact]
    public void Observe_DeduplicatesReplayedToolsAndDetectsBudgetMarker()
    {
        var collector = new MainAgentRunEvidenceCollector("agent-run-2");
        var call = new FunctionCallContent("call-2", "Read", null);
        var result = new FunctionResultContent("call-2", "ok");

        collector.Observe(Update(call, result));
        collector.Observe(Update(call, result));
        collector.Observe(Update(new TextContent("[Budget Exceeded] stopped")));

        collector.CompletedToolBatches.Should().ContainSingle();
        collector.CompletedToolBatches[0].Calls.Should().ContainSingle();
        collector.CompletedToolBatches[0].Results.Should().ContainSingle();
        collector.BudgetExceeded.Should().BeTrue();
    }

    [Fact]
    public void Observe_MultiTurnWithToolCalls_AccumulatesUsageAndPreservesStringToolErrorFlag()
    {
        var collector = new MainAgentRunEvidenceCollector("agent-run-3");

        // Turn 1: calls WebSearch (which fails and is unwrapped to string by ToolResultUnwrapMiddleware)
        collector.Observe(Update(
            new FunctionCallContent("call-err", "WebSearch", new Dictionary<string, object?> { ["query"] = "news" }),
            new UsageContent(new UsageDetails { InputTokenCount = 10_000, OutputTokenCount = 400 })));
        collector.Observe(Update(
            new FunctionResultContent("call-err", "[ERROR] WebSearch error: all providers failed.")));

        // Turn 2: calls Write and finishes
        collector.Observe(Update(
            new FunctionCallContent("call-ok", "Write", new Dictionary<string, object?> { ["path"] = "out.html" }),
            new UsageContent(new UsageDetails { InputTokenCount = 12_000, OutputTokenCount = 1_800 })));
        collector.Observe(Update(
            new FunctionResultContent("call-ok", """{"Content":"Wrote out.html","IsError":false}""")));

        // Turn 3: final summary
        collector.Observe(Update(
            new TextContent("Completed report."),
            new UsageContent(new UsageDetails { InputTokenCount = 15_000, OutputTokenCount = 60 })));

        collector.InputTokens.Should().Be(37_000, "input tokens across all 3 LLM turns must be accumulated");
        collector.OutputTokens.Should().Be(2_260, "output tokens from earlier tool-calling turns must not be overwritten by the final 60-token summary");
        collector.CompletedToolBatches.Should().HaveCount(2);
        collector.CompletedToolBatches[0].Results.Single().IsError.Should().BeTrue(
            "string tool results starting with [ERROR] must preserve IsError=true");
        collector.CompletedToolBatches[0].Results.Single().Completion.Should().Be(ToolResultCompletion.Failed);
        collector.CompletedToolBatches[1].Results.Single().IsError.Should().BeFalse();
    }

    private static AgentResponseUpdate Update(params AIContent[] contents)
    {
        var update = new AgentResponseUpdate();
        foreach (var content in contents)
            update.Contents.Add(content);
        return update;
    }
}
