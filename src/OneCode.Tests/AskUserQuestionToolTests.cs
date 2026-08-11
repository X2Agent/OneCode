using System.Text.Json;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Tools;
using OneCode.App.Tui;
using OneCode.Core.Models;
using OneCode.Core.Tools;

namespace OneCode.Tests;

// AskUserQuestionTool — headless / interactive 行为契约测试
//
// 防回归：确保 headless 回退路径返回 Error（而非伪装成用户答案的 Success），
// 并且 UserQuestionService 在 TuiInteractionBridge 未设置时让工具进入 Error 分支。

public sealed class AskUserQuestionToolTests
{
    // AskUserQuestionTool: headless 回退

    [Fact]
    public async Task AskAsync_NoService_ReturnsError_NotSuccess()
    {
        var tool = new AskUserQuestionTool(userQuestionService: null!);

        var result = await tool.AskAsync("Use EF Core or Dapper?", ["EF Core", "Dapper"]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("no interactive terminal");
        result.Content.Should().Contain("Use EF Core or Dapper?");
        // 关键防回归：不能出现伪装成用户答案的固定字符串
        result.Content.Should().NotContain("Answer: (no interactive terminal available)");
        result.SuggestedNextAction.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AskAsync_NoService_DoesNotReturnCannedAnswerString()
    {
        // 防止历史回归：旧的 NullAskUserQuestionSink 会返回
        // "Answer: (no interactive terminal available)" 这类伪装成用户答案的 Success。
        var tool = new AskUserQuestionTool(userQuestionService: null!);

        var result = await tool.AskAsync("Proceed?");

        result.IsError.Should().BeTrue();
        // 错误内容里不应出现 "Answer:" 前缀（那是真实回答才有的格式）
        result.Content.Should().NotContain("Answer:");
    }

    // AskUserQuestionTool: 服务返回 null（用户取消 / TUI 不可用）

    [Fact]
    public async Task AskAsync_ServiceReturnsNull_ReturnsError()
    {
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync("Pick one", ["A", "B"]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("did not provide an answer");
        result.SuggestedNextAction.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AskAsync_ServiceReturnsAnswer_ReturnsSuccessWithAnswer()
    {
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns("EF Core");
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync("Which ORM?", ["EF Core", "Dapper"]);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Question: Which ORM?");
        result.Content.Should().Contain("Answer: EF Core");
    }

    [Fact]
    public async Task AskAsync_PassesQuestionAndOptionsToService()
    {
        string? capturedQuestion = null;
        IReadOnlyList<string>? capturedOptions = null;
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskAsync(
                Arg.Do<string>(q => capturedQuestion = q),
                Arg.Do<IReadOnlyList<string>?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns("ok");
        var tool = new AskUserQuestionTool(svc);

        await tool.AskAsync("the question", ["o1", "o2"]);

        capturedQuestion.Should().Be("the question");
        capturedOptions.Should().BeEquivalentTo(["o1", "o2"]);
    }

    [Fact]
    public async Task AskMultipleAsync_PassesRelatedQuestionsAsSingleWizardRequest()
    {
        IReadOnlyList<WizardQuestion>? capturedQuestions = null;
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskMultipleAsync(
                "规划前确认",
                Arg.Do<IReadOnlyList<WizardQuestion>>(items => capturedQuestions = items),
                Arg.Any<CancellationToken>())
            .Returns(new WizardResult(new Dictionary<string, string>
            {
                ["scope"] = "OneCode.App",
                ["validation"] = "dotnet test",
            }));
        var tool = new AskUserQuestionTool(svc);
        using var scopeQuestion = JsonDocument.Parse("""{"id":"scope","question":"目标模块？"}""");
        using var validationQuestion = JsonDocument.Parse("""{"id":"validation","question":"验收命令？"}""");

        var result = await tool.AskMultipleAsync(
            "规划前确认",
            [scopeQuestion.RootElement, validationQuestion.RootElement]);

        result.IsError.Should().BeFalse();
        capturedQuestions.Should().HaveCount(2);
        capturedQuestions!.Select(item => item.Id).Should().Equal("scope", "validation");
        await svc.DidNotReceiveWithAnyArgs().AskAsync(default!, default, default);
    }

    // UserQuestionService: TuiInteractionBridge 未设置 EmitEvent 时返回 null

    [Fact]
    public async Task UserQuestionService_NoEmitEvent_ReturnsNull()
    {
        var bridge = new TuiInteractionBridge();
        var svc = new UserQuestionService(bridge);

        var answer = await svc.AskAsync("q", ["a", "b"]);

        answer.Should().BeNull();
    }

    [Fact]
    public async Task UserQuestionService_WithEmitEvent_EmitsRequestAndReturnsResponse()
    {
        TuiUserQuestionRequest? emitted = null;
        var bridge = new TuiInteractionBridge();
        bridge.SetEmitter(evt =>
        {
            emitted = (TuiUserQuestionRequest)evt;
            // 模拟 TUI 层回传用户答案
            emitted.ResponseSource.TrySetResult("Dapper");
        });
        var svc = new UserQuestionService(bridge);

        var answer = await svc.AskAsync("Which?", ["EF Core", "Dapper"]);

        answer.Should().Be("Dapper");
        emitted.Should().NotBeNull();
        emitted!.Question.Should().Be("Which?");
        emitted.Options.Should().BeEquivalentTo(["EF Core", "Dapper"]);
    }

    [Fact]
    public async Task UserQuestionService_Cancellation_PropagatesAndReturnsNull()
    {
        var bridge = new TuiInteractionBridge();
        bridge.SetEmitter(_ =>
        {
            // 不设置结果，等待取消
        });
        var svc = new UserQuestionService(bridge);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var answer = await svc.AskAsync("q", ct: cts.Token);

        answer.Should().BeNull();
    }

    // 端到端：Tool + UserQuestionService + 无 EmitEvent

    [Fact]
    public async Task EndToEnd_RealServiceWithoutEmitEvent_ToolReturnsError()
    {
        // 真实 UserQuestionService + bridge 未设置 EmitEvent → 工具应返回 Error
        var bridge = new TuiInteractionBridge();
        var svc = new UserQuestionService(bridge);
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync("any question?");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("did not provide an answer");
    }

    // helpers

    private static TuiContext CreateMinimalTuiContext(Action<TuiEvent>? emitEvent)
    {
        return new TuiContext(
            new TuiQueryServices(
                StreamQuery: (_, _, _) => AsyncEnumerable.Empty<TuiEvent>(),
                CreateSession: _ => Task.CompletedTask,
                ExecuteCommand: (_, _) => Task.FromResult<string?>(null)),
            new TuiSessionServices(),
            new TuiDiagnosticServices(),
            new TuiRuntimeServices(
                Model: "test",
                ModelCatalog: new ModelCatalogStore(),
                EmitEvent: emitEvent),
            new TuiLaunchOptions(
                Version: "test",
                ExternalCancellation: CancellationToken.None,
                SlashCommands: []));
    }

}
