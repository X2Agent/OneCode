#pragma warning disable MAAI001 // MAF 实验性 API（AIContextProvider.InvokingContext 构造）：升级 MAF 时按升级复查清单回归
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.Tests;

/// <summary>
/// <see cref="MemoryContextProvider"/> 行为契约：将有效记忆全文通过 <see cref="AIContext.Instructions"/>
/// 注入（不增加额外 System Message 块），并在读取失败时优雅降级、取消时透传。
/// </summary>
public sealed class MemoryContextProviderTests
{
    [Fact]
    public async Task InvokingAsync_WhenMemoriesExist_InjectsInstructionsWithoutExtraMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.LoadMemoryPromptAsync(Arg.Any<CancellationToken>())
            .Returns("## Memory\n\n### Learned knowledge\n- [fact] (project) `fact:build`: dotnet build src/OneCode.slnx");

        var sut = new MemoryContextProvider(memoryService, NullLogger<MemoryContextProvider>.Instance);
        var context = await sut.InvokingAsync(new AIContextProvider.InvokingContext(
            Substitute.For<AIAgent>(),
            session: null,
            new AIContext()), ct);

        context.Instructions.Should().Contain("fact:build");
        context.Instructions.Should().Contain("dotnet build src/OneCode.slnx");
        context.Messages.Should().BeNullOrEmpty("memory must be injected via Instructions to preserve Anthropic cache breakpoint budget");
    }

    [Fact]
    public async Task InvokingAsync_WhenNoMemoriesExist_ReturnsEmptyContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.LoadMemoryPromptAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var sut = new MemoryContextProvider(memoryService, NullLogger<MemoryContextProvider>.Instance);
        var context = await sut.InvokingAsync(new AIContextProvider.InvokingContext(
            Substitute.For<AIAgent>(),
            session: null,
            new AIContext()), ct);

        context.Instructions.Should().BeNull();
    }

    [Fact]
    public async Task InvokingAsync_WhenStoreThrows_DegradesGracefullyWithoutThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.LoadMemoryPromptAsync(Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new IOException("disk read error"));

        var sut = new MemoryContextProvider(memoryService, NullLogger<MemoryContextProvider>.Instance);
        var context = await sut.InvokingAsync(new AIContextProvider.InvokingContext(
            Substitute.For<AIAgent>(),
            session: null,
            new AIContext()), ct);

        context.Instructions.Should().BeNull("a corrupted memory store must not abort the main conversation turn");
    }

    [Fact]
    public async Task InvokingAsync_WhenCancelled_PropagatesCancellation()
    {
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.LoadMemoryPromptAsync(Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new OperationCanceledException());

        var sut = new MemoryContextProvider(memoryService, NullLogger<MemoryContextProvider>.Instance);
        var act = async () => await sut.InvokingAsync(new AIContextProvider.InvokingContext(
            Substitute.For<AIAgent>(),
            session: null,
            new AIContext()), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
