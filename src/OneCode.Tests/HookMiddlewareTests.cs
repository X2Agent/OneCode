using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// <see cref="HookMiddleware"/> 契约测试——覆盖：
///   pre_tool_call / post_tool_call 完整上下文（SessionId / Cwd / ToolUseId / ToolName / ToolInput）透传
///   → post_tool_call 兼容未解包 ToolResult 与经 ToolResultUnwrapMiddleware 解包后的字符串错误提取 ToolError
///   → pre_tool_call 阻断或抛异常时单工具 fail-closed（不调用 next、不触发 post_tool_call）
/// </summary>
public sealed class HookMiddlewareTests
{
    private static FunctionInvocationContext CreateContext(
        string toolName = "Bash",
        string callId = "call-42",
        IDictionary<string, object?>? args = null)
    {
        var function = Substitute.For<AIFunction>();
        function.Name.Returns(toolName);
        var argDict = (args ?? new Dictionary<string, object?> { ["command"] = "git status" })
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return new FunctionInvocationContext
        {
            Function = function,
            CallContent = new FunctionCallContent(callId, toolName, argDict),
            Arguments = new AIFunctionArguments(argDict),
        };
    }

    private static List<HookPayload> CapturedPayloads(IHookExecutionService hooks) =>
        [.. hooks.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IHookExecutionService.FireAsync))
            .Select(c => (HookPayload)c.GetArguments()[0]!)];

    [Fact]
    public async Task Create_PreAndPostToolCall_PopulatesSessionIdCwdAndToolUseId()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.NewId();
        var hooks = Substitute.For<IHookExecutionService>();
        hooks.FireAsync(Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AggregatedHookResult()));

        var options = new AgentPipelineOptions
        {
            WorkingDirectory = "/workspace/onecode",
            ConversationId = sessionId,
            HookExecutionService = hooks,
        };
        var middleware = HookMiddleware.Create(options, NullLogger.Instance);
        var ctx = CreateContext("Bash", "call-req-99");

        var result = await middleware(
            Substitute.For<AIAgent>(),
            ctx,
            (_, _) => new ValueTask<object?>("ok-output"),
            ct);

        result.Should().Be("ok-output");

        var payloads = CapturedPayloads(hooks);
        payloads.Should().HaveCount(2);

        payloads[0].Point.Should().Be(HookInterceptionPoint.PreToolCall);
        payloads[0].SessionId.Should().Be(sessionId.ToString());
        payloads[0].Cwd.Should().Be("/workspace/onecode");
        payloads[0].ToolName.Should().Be("Bash");
        payloads[0].ToolUseId.Should().Be("call-req-99");

        payloads[1].Point.Should().Be(HookInterceptionPoint.PostToolCall);
        payloads[1].SessionId.Should().Be(sessionId.ToString());
        payloads[1].Cwd.Should().Be("/workspace/onecode");
        payloads[1].ToolName.Should().Be("Bash");
        payloads[1].ToolUseId.Should().Be("call-req-99");
        payloads[1].ToolResponse.Should().Be("ok-output");
        payloads[1].ToolError.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"Content":"command timed out after 30s","IsError":true,"Severity":"error"}""", "command timed out after 30s")]
    [InlineData("[ERROR] File not found: /tmp/missing.txt", "[ERROR] File not found: /tmp/missing.txt")]
    [InlineData("Error: permission denied", "Error: permission denied")]
    [InlineData("""{"Content":"build succeeded","IsError":false}""", null)]
    [InlineData("normal plain text output", null)]
    public async Task Create_PostToolCall_ExtractsToolErrorFromUnwrappedStrings(
        string unwrappedToolReturn,
        string? expectedToolError)
    {
        var ct = TestContext.Current.CancellationToken;
        var hooks = Substitute.For<IHookExecutionService>();
        hooks.FireAsync(Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AggregatedHookResult()));

        var options = new AgentPipelineOptions
        {
            WorkingDirectory = "/workspace",
            HookExecutionService = hooks,
        };
        var middleware = HookMiddleware.Create(options, NullLogger.Instance);
        var ctx = CreateContext("Bash", "call-err-1");

        var result = await middleware(
            Substitute.For<AIAgent>(),
            ctx,
            (_, _) => new ValueTask<object?>(unwrappedToolReturn),
            ct);

        result.Should().Be(unwrappedToolReturn);

        var post = CapturedPayloads(hooks)
            .Single(p => p.Point == HookInterceptionPoint.PostToolCall);
        post.ToolError.Should().Be(expectedToolError);
    }

    [Fact]
    public async Task Create_PreToolCallDenied_ShortCircuitsWithoutExecutingToolOrFiringPostHook()
    {
        var ct = TestContext.Current.CancellationToken;
        var hooks = Substitute.For<IHookExecutionService>();
        hooks.FireAsync(Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AggregatedHookResult
            {
                BlockingErrors = [new HookBlockingError("rm is forbidden", "security-gate")],
            }));

        var options = new AgentPipelineOptions
        {
            WorkingDirectory = "/workspace",
            HookExecutionService = hooks,
        };
        var middleware = HookMiddleware.Create(options, NullLogger.Instance);
        var nextInvoked = false;

        var result = await middleware(
            Substitute.For<AIAgent>(),
            CreateContext("Bash", "call-blocked"),
            (_, _) =>
            {
                nextInvoked = true;
                return new ValueTask<object?>("should-not-run");
            },
            ct);

        nextInvoked.Should().BeFalse("pre_tool_call deny 必须阻止工具实际执行");
        result.Should().BeOfType<ToolResult>()
            .Which.Should().Match<ToolResult>(tr =>
                tr.IsError && tr.Content.Contains("rm is forbidden"));
        CapturedPayloads(hooks).Should().ContainSingle()
            .Which.Point.Should().Be(HookInterceptionPoint.PreToolCall);
    }
}
