using NSubstitute;
using OneCode.App.Services.Interaction;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

// AskUserQuestionTool 行为契约测试。
//
// 防回归：服务返回空（用户取消 / 无头宿主无接收者）时返回 Error 而非伪装成
// 用户答案的 Success；单题与向导的参数校验与服务分派各就各位。

public sealed class AskUserQuestionToolTests
{
    // AskUserQuestionTool: 参数校验

    [Fact]
    public async Task AskAsync_NoQuestions_ReturnsError()
    {
        var tool = new AskUserQuestionTool(Substitute.For<IUserQuestionService>());

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

    // AskUserQuestionTool: 服务返回 null（用户取消 / 无头宿主无接收者）

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
        // 防回归：错误内容里不得出现 "Answer:" 前缀（那是真实回答才有的格式），
        // 也不得复现旧实现的伪装答案字符串。
        result.Content.Should().NotContain("Answer:");
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

    // 端到端：真实提问端口 + 无头通道（无接收者）→ 工具返回 Error

    [Fact]
    public async Task AskAsync_HeadlessPortNoReceiver_ReturnsError()
    {
        // 真实交互端口 + 无头 dispatcher（TrySend 恒 false）→ 工具应返回 Error
        var svc = new InteractionUserQuestionService(new HeadlessInteractionDispatcher());
        var tool = new AskUserQuestionTool(svc);

        var result = await tool.AskAsync(questions: [new AskQuestionInput("any question?")]);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("did not provide an answer");
    }
}