using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// IsConcurrencySafe=false 串行闸守卫：并行调用非并发安全工具时在途并发不得超过 1
///（否则两个并行 Write 会交错写同一文件）；并发安全工具与未知工具不受闸影响。
/// </summary>
public sealed class UnsafeToolSerialGateMiddlewareTests
{
    [Fact]
    public async Task Invoke_ConcurrentUnsafeToolCalls_NeverOverlap()
    {
        var maxInFlight = await RunConcurrentAsync(
            CreateMetadata(("Write", IsConcurrencySafe: false)),
            "Write",
            parallelism: 4);

        maxInFlight.Should().Be(1, "非并发安全工具必须串行执行，不得出现交错写");
    }

    [Fact]
    public async Task Invoke_ConcurrentSafeToolCalls_RunInParallel()
    {
        var maxInFlight = await RunConcurrentAsync(
            CreateMetadata(("Read", IsConcurrencySafe: true)),
            "Read",
            parallelism: 4);

        maxInFlight.Should().Be(4, "并发安全工具不得被闸阻塞");
    }

    [Fact]
    public async Task Invoke_ToolWithoutMetadata_PassesThroughWithoutGate()
    {
        // Harness provider 工具（todos_* 等）不在静态目录内——缺元数据不得误闸
        var maxInFlight = await RunConcurrentAsync(
            new UnsafeToolSerialGateMiddleware(),
            "todos_write",
            parallelism: 2);

        maxInFlight.Should().Be(2, "无元数据的工具直接放行");
    }

    private static UnsafeToolSerialGateMiddleware CreateMetadata(params (string Name, bool IsConcurrencySafe)[] tools)
    {
        var metadata = new ToolMetadataRegistry();
        foreach (var (name, isConcurrencySafe) in tools)
        {
            metadata.Register(new ToolMetadata
            {
                Name = name,
                Risk = ToolRisk.ReadOnly,
                IsConcurrencySafe = isConcurrencySafe,
            });
        }
        return new UnsafeToolSerialGateMiddleware(metadata);
    }

    /// <summary>并发调用 <paramref name="toolName"/> 并返回在途并发的最大观测值。</summary>
    private static async Task<int> RunConcurrentAsync(
        UnsafeToolSerialGateMiddleware middleware,
        string toolName,
        int parallelism)
    {
        var inFlight = 0;
        var maxInFlight = 0;

        async ValueTask<object?> Next(FunctionInvocationContext ctx, CancellationToken ct)
        {
            var observed = Interlocked.Increment(ref inFlight);
            while (true)
            {
                var current = Volatile.Read(ref maxInFlight);
                if (observed <= current)
                    break;
                if (Interlocked.CompareExchange(ref maxInFlight, observed, current) == current)
                    break;
            }

            await Task.Delay(150, ct);
            Interlocked.Decrement(ref inFlight);
            return "ok";
        }

        var gate = middleware.CreateDelegate();
        var agent = Substitute.For<AIAgent>();
        var invocations = Enumerable.Range(0, parallelism)
            .Select(_ => gate(agent, Context(toolName), Next, TestContext.Current.CancellationToken).AsTask())
            .ToList();
        await Task.WhenAll(invocations);
        return Volatile.Read(ref maxInFlight);
    }

    private static FunctionInvocationContext Context(string name)
    {
        var function = Substitute.For<AIFunction>();
        function.Name.Returns(name);
        return new FunctionInvocationContext { Function = function };
    }
}
