using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;

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

    private static AgentResponseUpdate Update(params AIContent[] contents)
    {
        var update = new AgentResponseUpdate();
        foreach (var content in contents)
            update.Contents.Add(content);
        return update;
    }
}
