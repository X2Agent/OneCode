using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

public sealed class ClarificationQuestionGeneratorTests
{
    [Fact]
    public void Parse_IgnoresMarkdownFenceAndTruncatesToFive()
    {
        var text = """
            ```json
            {"questions":["q1","q2","q3","q4","q5","q6"],"inScope":[],"acceptanceCriteria":[],"constraints":[]}
            ```
            """;

        var intake = ClarificationQuestionGenerator.Parse(text);

        intake.Questions.Should().Equal("q1", "q2", "q3", "q4", "q5");
    }

    [Fact]
    public void Parse_ExtractsBaselineFieldsAlongsideQuestions()
    {
        var intake = ClarificationQuestionGenerator.Parse(
            """{"questions":["q1"],"inScope":["src/App"],"acceptanceCriteria":["构建通过"],"constraints":[".NET 8"]}""");

        intake.Questions.Should().Equal("q1");
        intake.InScope.Should().Equal("src/App");
        intake.AcceptanceCriteria.Should().Equal("构建通过");
        intake.Constraints.Should().Equal(".NET 8");
    }

    [Fact]
    public void Parse_EmptyQuestions_ThrowsWithoutFallback()
    {
        var act = () => ClarificationQuestionGenerator.Parse("""{"questions":[],"inScope":["x"]}""");

        act.Should().Throw<InvalidOperationException>().WithMessage("*澄清问题生成失败*");
    }

    [Fact]
    public void Parse_UnparseableOutput_ThrowsWithoutFallback()
    {
        var act = () => ClarificationQuestionGenerator.Parse("模型没有输出 JSON");

        act.Should().Throw<InvalidOperationException>().WithMessage("*澄清问题生成失败*");
    }

    [Fact]
    public async Task GenerateAsync_ReturnsModelIntake()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"questions":["第一期做需求生成还是评审？"],"inScope":[],"acceptanceCriteria":["验收通过"],"constraints":[]}""")));
        var prompts = Substitute.For<IPromptManager>();
        prompts.GetPromptOrDefaultAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns("ask questions");
        var sut = new ClarificationQuestionGenerator(chat, prompts);
        var assessment = new RequirementAssessmentService().Assess("开发一个产研 AI 系统");

        var intake = await sut.GenerateAsync(
            "开发一个产研 AI 系统",
            assessment,
            TestContext.Current.CancellationToken);

        intake.Questions.Should().Equal("第一期做需求生成还是评审？");
        intake.AcceptanceCriteria.Should().Equal("验收通过");
    }

    [Fact]
    public async Task GenerateAsync_ModelFailure_ThrowsWithoutFallback()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ChatResponse>(new InvalidOperationException("model down")));
        var prompts = Substitute.For<IPromptManager>();
        prompts.GetPromptOrDefaultAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns("ask questions");
        var sut = new ClarificationQuestionGenerator(chat, prompts);
        var assessment = new RequirementAssessmentService().Assess("开发一个产研 AI 系统");

        var act = () => sut.GenerateAsync(
            "开发一个产研 AI 系统",
            assessment,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*澄清问题生成失败*");
    }
}
