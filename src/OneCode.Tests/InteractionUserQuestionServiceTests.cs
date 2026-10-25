using OneCode.App.Services.Interaction;
using OneCode.Core.Tools;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// InteractionUserQuestionService——端口级契约测试：无接收者返回空、
/// 请求投递与回答回传（经 TuiInteractionDispatcher 真实通道）、取消降级为空结果。
/// </summary>
public sealed class InteractionUserQuestionServiceTests
{
    [Fact]
    public async Task AskAsync_NoReceiver_ReturnsNull()
    {
        var service = new InteractionUserQuestionService(new HeadlessInteractionDispatcher());

        var answer = await service.AskAsync("q", ["a", "b"]);

        answer.Should().BeNull("无接收者时由调用工具按「用户未回答」降级");
    }

    [Fact]
    public async Task AskAsync_ReceiverAnswers_ReturnsAnswerAndDeliversRequest()
    {
        QuestionInteractionRequest? delivered = null;
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(request =>
        {
            delivered = (QuestionInteractionRequest)request;
            delivered.ResponseSource.TrySetResult("Dapper");
        });
        var service = new InteractionUserQuestionService(dispatcher);

        var answer = await service.AskAsync("Which?", ["EF Core", "Dapper"]);

        answer.Should().Be("Dapper");
        delivered.Should().NotBeNull();
        delivered!.Question.Should().Be("Which?");
        delivered.Options.Should().BeEquivalentTo(["EF Core", "Dapper"]);
    }

    [Fact]
    public async Task AskAsync_CancelledWhileWaiting_ReturnsNull()
    {
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(_ =>
        {
            // 不回传答案，等待取消
        });
        var service = new InteractionUserQuestionService(dispatcher);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var answer = await service.AskAsync("q", ct: cts.Token);

        answer.Should().BeNull("取消与无接收者同语义：由调用工具降级，不向引擎抛异常");
    }

    [Fact]
    public async Task AskMultipleAsync_NoReceiver_ReturnsCancelled()
    {
        var service = new InteractionUserQuestionService(new HeadlessInteractionDispatcher());

        var result = await service.AskMultipleAsync("向导", [new WizardQuestion("a", "第一题")]);

        result.Should().Be(WizardResult.Cancelled);
    }
}
