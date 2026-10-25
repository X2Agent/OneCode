using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.Infrastructure;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Config;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// 覆盖 run 异常收场的 PromptTooLong 提示注入：上下文超限的失败必须带可操作的
/// /compact 指引（检测经 PromptTooLongDetector：HTTP 413 与 provider 关键词），
/// 其他异常不得误附提示——误导用户去压缩一个与上下文无关的失败。
/// </summary>
public sealed class QueryStreamEnginePromptTooLongHintTests
{
    [Fact]
    public async Task StreamInteractiveAsync_PromptTooLongError_CarriesCompactHint()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new HttpRequestException(
            "request failed",
            inner: null,
            statusCode: HttpStatusCode.RequestEntityTooLarge));

        var events = await CollectAsync(engine, ct);

        events.OfType<ErrorEvent>().Should().ContainSingle()
            .Which.Message.Should()
            .Contain("request failed").And.Contain("/compact",
                "上下文超限对用户是可自愈的：提示 /compact 才有下一步动作");
    }

    [Fact]
    public async Task StreamInteractiveAsync_KeywordError_CarriesCompactHint()
    {
        // provider 关键词路径（非 HttpRequestException）同样要命中提示。
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new InvalidOperationException("context_length_exceeded: 200000 > 128000"));

        var events = await CollectAsync(engine, ct);

        events.OfType<ErrorEvent>().Should().ContainSingle()
            .Which.Message.Should().Contain("/compact");
    }

    [Fact]
    public async Task StreamInteractiveAsync_UnrelatedError_HasNoCompactHint()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new InvalidOperationException("provider quota exhausted"));

        var events = await CollectAsync(engine, ct);

        events.OfType<ErrorEvent>().Should().ContainSingle()
            .Which.Message.Should()
            .Contain("provider quota exhausted").And.NotContain("/compact",
                "与上下文无关的失败不得引导用户压缩会话");
    }

    private static async Task<List<QueryEvent>> CollectAsync(QueryStreamEngine engine, CancellationToken ct)
    {
        var events = new List<QueryEvent>();
        await foreach (var evt in engine.StreamInteractiveAsync(
            messages: [new ChatMessage(ChatRole.User, "hello")],
            systemPrompt: "system",
            modelId: "test-model",
            ct: ct))
        {
            events.Add(evt);
        }
        return events;
    }

    private static QueryStreamEngine CreateEngine(Exception runFailure)
    {
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => []),
            toolMetadata,
            mcpConnectionManager: null);

        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings { MaxTurns = 100 }));

        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());

        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Do<System.Threading.Channels.ChannelWriter<object>>(writer => writer.Complete()),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(runFailure);

        return new QueryStreamEngine(
            NullLogger.Instance,
            runner,
            toolCatalog,
            Substitute.For<IHookExecutionService>(),
            new ChatSessionDependencies(
                sessionManager,
                new SessionToolSetManager(toolCatalog, toolMetadata),
                new ToolCapabilityResolver(toolCatalog),
                configManager),
            new ChatObservabilityDependencies(
                Substitute.For<ITokenUsageTracker>(),
                new TokenBreakdownEstimator(TestTokenEstimators.Default),
                Substitute.For<INotifierService>()));
    }
}
