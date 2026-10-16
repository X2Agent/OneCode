using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

public sealed class ToolCallEventMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ChineseArguments_EmitsReadableToolInput()
    {
        var events = new List<OrchestrationEvent>();
        var options = new AgentPipelineOptions
        {
            WorkingDirectory = Environment.CurrentDirectory,
            OrchestrationEventSink = events.Add,
        };
        var function = Substitute.For<AIFunction>();
        function.Name.Returns("AskUserQuestion");
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments
            {
                ["question"] = "请选择产品形态",
            },
        };
        var agent = Substitute.For<AIAgent>();
        agent.Name.Returns("agent-1");
        var middleware = ToolCallEventMiddleware.Create(options, NullLogger.Instance);

        await middleware(
            agent,
            context,
            (_, _) => new ValueTask<object?>("ok"),
            TestContext.Current.CancellationToken);

        var toolStart = events.OfType<OrchestrationEvent.ToolStart>().Single();
        toolStart.ToolInput.Should().Contain("请选择产品形态");
        toolStart.ToolInput.Should().NotContainEquivalentOf("\\u8BF7");
    }

    [Fact]
    public async Task InvokeAsync_SerializedToolResultEnvelope_UnwrapsContentAndPropagatesIsError()
    {
        var events = new List<OrchestrationEvent>();
        var options = new AgentPipelineOptions
        {
            WorkingDirectory = Environment.CurrentDirectory,
            OrchestrationEventSink = events.Add,
        };
        var function = Substitute.For<AIFunction>();
        function.Name.Returns("Bash");
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments { ["command"] = "curl -s https://example.com" },
        };
        var agent = Substitute.For<AIAgent>();
        agent.Name.Returns("agent-1");
        var middleware = ToolCallEventMiddleware.Create(options, NullLogger.Instance);

        await middleware(
            agent,
            context,
            (_, _) => new ValueTask<object?>("""{"content":"line1\nline2","isError":true}"""),
            TestContext.Current.CancellationToken);

        var toolDone = events.OfType<OrchestrationEvent.ToolDone>().Single();
        toolDone.Result.Should().Be("line1\nline2", "serialized ToolResult envelope must be unwrapped before reaching TUI");
        toolDone.IsError.Should().BeTrue("isError flag inside envelope must be preserved");
    }
}
