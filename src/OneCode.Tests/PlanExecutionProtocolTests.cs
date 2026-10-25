using OneCode.App.Services.PlanMode;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

public sealed class PlanExecutionProtocolTests
{
    private const string PlanMarkdown = "# Unique plan body 7f3a";

    [Fact]
    public void BuildTriggerInstruction_DoesNotRepeatPlanBody()
    {
        // 计划全文只经每轮注入的交接上下文携带一份；触发语再带一份会让首轮上下文翻倍。
        var text = PlanExecutionProtocol.BuildTriggerInstruction(CreateSnapshot());

        text.Should().NotContain(PlanMarkdown);
        text.Should().Contain("revision 2");
    }

    [Fact]
    public void BuildHandoffContext_CarriesPlanOnceWithOriginalRequestAndClarifications()
    {
        var text = PlanExecutionProtocol.BuildHandoffContext(CreateSnapshot());

        text.Split(PlanMarkdown).Should().HaveCount(2, "计划正文恰好出现一次");
        text.Should().Contain("must run offline");
        text.Should().Contain("Q&A: storage? -> sqlite");
        text.Should().Contain("- s1: Step one");
        text.Should().Contain("todos_complete");
    }

    [Fact]
    public void BuildHandoffContext_EmptyHandoff_OmitsUserSections()
    {
        var snapshot = CreateSnapshot() with { OriginalRequest = "", Clarifications = [] };

        var text = PlanExecutionProtocol.BuildHandoffContext(snapshot);

        text.Should().NotContain("USER'S ORIGINAL REQUEST");
        text.Should().NotContain("CLARIFICATIONS AND FEEDBACK");
        text.Should().Contain("IMMUTABLE APPROVED PLAN");
    }

    private static ApprovedPlanSnapshot CreateSnapshot() => new()
    {
        PlanId = PlanWorkflowId.NewId(),
        SessionId = SessionId.NewId(),
        Revision = 2,
        Title = "Plan",
        Markdown = PlanMarkdown,
        Steps =
        [
            new PlanStepDefinition
            {
                Id = "s1",
                Title = "Step one",
                Description = "Do it",
                Files = [],
                AcceptanceCriteria = ["done"],
                DependsOn = [],
                Risk = PlanStepRisk.Low,
            },
        ],
        ContentHash = "sha256-x",
        ApprovedBy = "user",
        ApprovedAt = DateTimeOffset.UtcNow,
        OriginalRequest = "the app must run offline",
        Clarifications = ["Q&A: storage? -> sqlite"],
    };
}
