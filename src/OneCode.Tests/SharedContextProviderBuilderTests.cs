using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Lsp;
using OneCode.App.Services.Skills;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Memory;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// Plan 模式装配期剔除 execute_code：CodeAct provider 按 WorkingMode 自检跳过，
/// 工具因此不进模型工具列表（比权限层事后拒绝少一整段审批往返）。
/// </summary>
public sealed class SharedContextProviderBuilderTests
{
    [Fact]
    public void BuildCommon_PlanMode_SkipsCodeActProviderWithoutProbing()
    {
        var (sut, codeActService, codeActProvider) = CreateSut();

        var providers = sut.BuildCommon(PipelineProfile.Full, Options(WorkingMode.Plan));

        providers.Should().NotContain(codeActProvider,
            "Plan mode must not advertise execute_code to the model at all");
        codeActService.DidNotReceive().TryCreateProvider(Arg.Any<string>());
    }

    [Fact]
    public void BuildCommon_BuildMode_IncludesCodeActProvider()
    {
        var (sut, codeActService, codeActProvider) = CreateSut();

        var providers = sut.BuildCommon(PipelineProfile.Full, Options(WorkingMode.Build));

        providers.Should().Contain(codeActProvider, "the sandboxed interpreter stays available in Build mode");
        codeActService.Received(1).TryCreateProvider(Arg.Any<string>());
    }

    [Fact]
    public void BuildCommon_UnspecifiedWorkingMode_IncludesCodeActProvider()
    {
        var (sut, codeActService, codeActProvider) = CreateSut();

        var providers = sut.BuildCommon(PipelineProfile.Full, Options(workingMode: null));

        providers.Should().Contain(codeActProvider,
            "callers that do not carry a mode (legacy/standalone paths) keep the default surface");
        codeActService.Received(1).TryCreateProvider(Arg.Any<string>());
    }

    [Fact]
    public void BuildCommon_SuppressedApprovalRun_SkipsCodeActProviderWithoutProbing()
    {
        var (sut, codeActService, codeActProvider) = CreateSut();

        var providers = sut.BuildCommon(
            PipelineProfile.Full, Options(WorkingMode.Build, suppressToolApproval: true));

        providers.Should().NotContain(codeActProvider,
            "a run without an approval channel must not mount tools that force an unanswerable approval request");
        codeActService.DidNotReceive().TryCreateProvider(Arg.Any<string>());
    }

    private static AgentContextProviderOptions Options(
        WorkingMode? workingMode,
        bool suppressToolApproval = false) => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        WorkingMode = workingMode,
        SuppressToolApproval = suppressToolApproval,
    };

    private static (SharedContextProviderBuilder Sut, IHyperlightCodeActService CodeActService, AIContextProvider CodeActProvider)
        CreateSut()
    {
        var codeActService = Substitute.For<IHyperlightCodeActService>();
        AIContextProvider codeActProvider = new StubCodeActProvider();
        codeActService.TryCreateProvider(Arg.Any<string>()).Returns(codeActProvider);

        var runtime = new AgentRuntimeContextDependencies(
            new ConversationShellExecutorManager(NullLogger<ConversationShellExecutorManager>.Instance),
            codeActService,
            new LspDiagnosticRegistry());

        var sut = new SharedContextProviderBuilder(
            NullLoggerFactory.Instance,
            new SkillProviderFactory(
                new SkillCatalog(Path.GetTempPath()),
                Substitute.For<IMcpConnectionManager>(),
                NullLoggerFactory.Instance),
            new AgentMemoryDependencies(
                Substitute.For<IMemoryService>(),
                Substitute.For<ISessionManager>()),
            runtime);

        return (sut, codeActService, codeActProvider);
    }

    /// <summary>CodeAct provider 桩：AIContextProvider 无无参构造，NSubstitute 代理不了，用最小子类。</summary>
    private sealed class StubCodeActProvider : OneCode.App.Services.Context.ReadOnlyAIContextProviderBase
    {
    }
}