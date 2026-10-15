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

        var result = await tool.AskAsync(questions: [new AskQuestionInput("Use EF Core or Dapper?", Options: ["EF Core", "Dapper"])]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("no interactive terminal");
        result.Content.Should().Contain("Use EF Core or Dapper?");
        // 关键防回归：不能出现伪装成用户答案的固定字符串
        result.Content.Should().NotContain("Answer: (no interactive terminal available)");
        result.SuggestedNextAction.Should().Be("Choose reasonable defaults or skip the step that required user input.",
            "headless 错误的后续动作必须是安全默认/跳过，而不是把错误当答案");
    }

    [Fact]
    public async Task AskAsync_NoService_DoesNotReturnCannedAnswerString()
    {
        // 防止历史回归：旧的 NullAskUserQuestionSink 会返回
        // "Answer: (no interactive terminal available)" 这类伪装成用户答案的 Success。
        var tool = new AskUserQuestionTool(userQuestionService: null!);

        var result = await tool.AskAsync(questions: [new AskQuestionInput("Proceed?")]);

        result.IsError.Should().BeTrue();
        // 错误内容里不应出现 "Answer:" 前缀（那是真实回答才有的格式）
        result.Content.Should().NotContain("Answer:");
    }

    // AskUserQuestionTool: 参数校验

    [Fact]
    public async Task AskAsync_NoQuestions_ReturnsError()
    {
        var tool = new AskUserQuestionTool(userQuestionService: null!);

        var result = await tool.AskAsync();

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("no questions provided");
    }

    [Fact]
    public async Task AskAsync_MultipleQuestions_MissingId_ReturnsError()
    {
        var svc = Substitute.For<IUserQuestionService>();
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync(
            "向导",
            [new AskQuestionInput("第一题", Id: "first"), new AskQuestionInput("第二题")]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("'id' and 'question'");
        await svc.DidNotReceiveWithAnyArgs().AskMultipleAsync(default!, default!, default);
    }

    // AskUserQuestionTool: 服务返回 null（用户取消 / TUI 不可用）

    [Fact]
    public async Task AskAsync_ServiceReturnsNull_ReturnsError()
    {
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync(questions: [new AskQuestionInput("Pick one", Options: ["A", "B"])]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("did not provide an answer");
        result.SuggestedNextAction.Should().Be("Choose a reasonable default or skip the step that required user input.",
            "取消/无 UI 场景的后续动作必须是安全默认/跳过");
    }

    [Fact]
    public async Task AskAsync_ServiceReturnsAnswer_ReturnsSuccessWithAnswer()
    {
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns("EF Core");
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync(questions: [new AskQuestionInput("Which ORM?", Options: ["EF Core", "Dapper"])]);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Question: Which ORM?");
        result.Content.Should().Contain("Answer: EF Core");
    }

    [Fact]
    public async Task AskAsync_SingleQuestion_PassesQuestionAndOptionsToService()
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

        await tool.AskAsync(questions: [new AskQuestionInput("the question", Options: ["o1", "o2"])]);

        capturedQuestion.Should().Be("the question");
        capturedOptions.Should().BeEquivalentTo(["o1", "o2"]);
    }

    [Fact]
    public async Task AskAsync_SingleQuestion_IgnoresTitle_GoesToSimpleDialog()
    {
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns("ok");
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync("向导标题", [new AskQuestionInput("唯一一题", Id: "only")]);

        result.IsError.Should().BeFalse();
        await svc.DidNotReceiveWithAnyArgs().AskMultipleAsync(default!, default!, default);
    }

    // AskUserQuestionTool: 向导（2+ 题）

    [Fact]
    public async Task AskAsync_MultipleQuestions_PassesRelatedQuestionsAsSingleWizardRequest()
    {
        string? capturedTitle = null;
        IReadOnlyList<WizardQuestion>? capturedQuestions = null;
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskMultipleAsync(
                Arg.Do<string>(t => capturedTitle = t),
                Arg.Do<IReadOnlyList<WizardQuestion>>(items => capturedQuestions = items),
                Arg.Any<CancellationToken>())
            .Returns(new WizardResult(new Dictionary<string, string>
            {
                ["scope"] = "OneCode.App",
                ["validation"] = "dotnet test",
            }));
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync(
            "规划前确认",
            [new AskQuestionInput("目标模块？", Id: "scope"), new AskQuestionInput("验收命令？", Id: "validation")]);

        result.IsError.Should().BeFalse();
        capturedTitle.Should().Be("规划前确认");
        capturedQuestions.Should().HaveCount(2);
        capturedQuestions!.Select(item => item.Id).Should().Equal("scope", "validation");
        await svc.DidNotReceiveWithAnyArgs().AskAsync(default!, default, default);
    }

    [Fact]
    public async Task AskAsync_MultipleQuestions_WizardCancelled_ReturnsError()
    {
        var svc = Substitute.For<IUserQuestionService>();
        svc.AskMultipleAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<WizardQuestion>>(), Arg.Any<CancellationToken>())
            .Returns(WizardResult.Cancelled);
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync(
            "规划前确认",
            [new AskQuestionInput("一", Id: "a"), new AskQuestionInput("二", Id: "b")]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("cancelled by user");
        result.Content.Should().NotContain("Answers:");
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

        var result = await tool.AskAsync(questions: [new AskQuestionInput("any question?")]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("did not provide an answer");
    }
}
