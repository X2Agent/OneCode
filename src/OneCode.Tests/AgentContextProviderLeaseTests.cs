using Microsoft.Agents.AI;
using OneCode.App.Services.Agent;

namespace OneCode.Tests;

/// <summary>
/// §4.3.3 行为契约：每 run 创建的 provider 必须有宿主释放方。
/// </summary>
/// <remarks>
/// MAF 的 <c>ChatClientAgent</c> 不释放交给它的 <c>AIContextProviders</c>。技能 provider 每 run 重建
/// 并持有真实资源（<c>CachingAgentSkillsSource</c> 的信号量门、MCP 技能源的 reconcile gate），
/// 交给 GC 会让存活门的数量取决于终结时机，而不是实际发生过多少次 run。
/// </remarks>
public sealed class AgentContextProviderLeaseTests
{
    [Fact]
    public void Track_DisposesEveryDisposableProvider()
    {
        var first = new TrackableProvider();
        var second = new TrackableProvider();

        using (var lease = AgentContextProviderLease.Track([first, second]))
        {
            lease.Count.Should().Be(2);
        }

        first.DisposeCount.Should().Be(1);
        second.DisposeCount.Should().Be(1);
    }

    /// <summary>
    /// 反证：DI 单例（内存检索、LSP 诊断等）不实现 IDisposable，租约不得尝试释放它们——
    /// 释放共享单例会让下一次 run 拿到已失效的 provider。
    /// </summary>
    [Fact]
    public void Track_NonDisposableProviders_AreNotOwned()
    {
        var shared = new NonDisposableProvider();

        using var lease = AgentContextProviderLease.Track([shared]);

        lease.Count.Should().Be(0, "shared providers outlive the run and are not the lease's to release");
    }

    /// <summary>释放必须幂等：run 的正常路径与 finally 可能都触发一次。</summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        var provider = new TrackableProvider();
        var lease = AgentContextProviderLease.Track([provider]);

        lease.Dispose();
        lease.Dispose();

        provider.DisposeCount.Should().Be(1);
    }

    /// <summary>
    /// 反证：一个 provider 释放失败不得让其余的泄漏。
    /// </summary>
    [Fact]
    public void Dispose_OneFailure_StillDisposesTheRest()
    {
        var failing = new TrackableProvider(throwOnDispose: true);
        var healthy = new TrackableProvider();

        AgentContextProviderLease.Track([failing, healthy]).Dispose();

        healthy.DisposeCount.Should().Be(1, "a failing release must not strand the others");
    }

    /// <summary>
    /// Minimal provider double that owns resources, mirroring the per-run skills provider.
    /// <see cref="AIContextProvider"/> has no parameterless constructor and cannot be substituted.
    /// </summary>
    private sealed class TrackableProvider(bool throwOnDispose = false) : AIContextProvider, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;

            if (throwOnDispose)
                throw new InvalidOperationException("simulated release failure");
        }

        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
            => new(new AIContext());
    }

    /// <summary>Provider double shaped like the DI singletons the lease must leave alone.</summary>
    private sealed class NonDisposableProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
            => new(new AIContext());
    }
}
