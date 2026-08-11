using NSubstitute;
using OneCode.App.Services;
using OneCode.Core.Tools;

namespace OneCode.Tests;

public sealed class ClarificationInteractionServiceTests
{
    [Fact]
    public async Task AskAsync_SingleQuestion_UsesExistingSingleQuestionFlow()
    {
        var questions = Substitute.For<IUserQuestionService>();
        questions.AskAsync("修改哪个范围？", null, Arg.Any<CancellationToken>())
            .Returns("仅修改 ChatService");
        var sut = new ClarificationInteractionService(questions);

        var result = await sut.AskAsync(
            "开始执行前需要确认",
            ["修改哪个范围？"],
            ct: TestContext.Current.CancellationToken);

        result.Should().Be(new ClarificationInteractionResult("仅修改 ChatService", false));
        await questions.DidNotReceiveWithAnyArgs().AskMultipleAsync(default!, default!, default);
    }

    [Fact]
    public async Task AskAsync_MultipleQuestions_UsesExistingQuestionWizard()
    {
        var questions = Substitute.For<IUserQuestionService>();
        questions.AskMultipleAsync(
                "团队任务需要补充信息",
                Arg.Any<IReadOnlyList<WizardQuestion>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var items = callInfo.ArgAt<IReadOnlyList<WizardQuestion>>(1);
                return new WizardResult(new Dictionary<string, string>
                {
                    [items[0].Id] = "OneCode.App",
                    [items[1].Id] = "dotnet test",
                });
            });
        var sut = new ClarificationInteractionService(questions);

        var result = await sut.AskAsync(
            "团队任务需要补充信息",
            ["目标模块？", "验收命令？"],
            ct: TestContext.Current.CancellationToken);

        result.IsCancelled.Should().BeFalse();
        result.Response.Should().Contain("目标模块？\nOneCode.App");
        result.Response.Should().Contain("验收命令？\ndotnet test");
        await questions.DidNotReceiveWithAnyArgs().AskAsync(default!, default, default);
    }

    [Fact]
    public async Task AskAsync_Confirmation_MapsSelectionToStableCoordinatorResponse()
    {
        var questions = Substitute.For<IUserQuestionService>();
        questions.AskAsync(
                "确认建议范围？",
                Arg.Is<IReadOnlyList<string>>(options => options.SequenceEqual(new[] { "确认执行", "取消" })),
                Arg.Any<CancellationToken>())
            .Returns("确认执行");
        var sut = new ClarificationInteractionService(questions);

        var result = await sut.AskAsync(
            "开始执行前需要确认",
            ["确认建议范围？"],
            confirmationOnly: true,
            TestContext.Current.CancellationToken);

        result.Should().Be(new ClarificationInteractionResult("确认执行", false));
    }

    [Fact]
    public async Task AskAsync_CancelledConfirmation_DoesNotForgeAnswer()
    {
        var questions = Substitute.For<IUserQuestionService>();
        questions.AskAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns("取消");
        var sut = new ClarificationInteractionService(questions);

        var result = await sut.AskAsync(
            "开始执行前需要确认",
            ["确认建议范围？"],
            confirmationOnly: true,
            TestContext.Current.CancellationToken);

        result.Should().Be(ClarificationInteractionResult.Cancelled);
    }
}
