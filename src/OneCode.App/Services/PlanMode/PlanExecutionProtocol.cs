using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// 批准计划执行轮的指令文本单一真相源：首跑触发语、每轮注入的交接上下文、收尾纠偏提示。
/// 文本与「步骤进度 = agent 待办清单」的机制强耦合（待办标题带步骤 id 前缀、<c>todos_complete</c> 勾选），
/// 属机器契约而非可调优 prompt——按 App/AGENTS.md 不纳入 prompts/ 文件化覆盖体系，集中于此防漂移。
/// </summary>
public static class PlanExecutionProtocol
{
    /// <summary>
    /// 工具边界句。计划阶段历史里反复出现 SubmitPlan 调用（plan.prompt 强约束模型提交计划），
    /// 本 run 的工具面不挂载它（PlanExclusive）——模型回放历史调用会浪费一轮往返。
    /// 事先声明边界，事前掐掉这类调用。
    /// </summary>
    public const string SubmitPlanUnavailableLine =
        "SubmitPlan is NOT available in this run — the plan is already approved; never call it.";

    private const string ExecutionRules =
        "Execute this approved snapshot in dependency order. Where the plan and earlier chat wording " +
        "conflict, the plan wins; the user's original request is the source for any constraint the plan omits. " +
        "Your todo list is already pre-seeded with one item per plan step (titled \"{stepId}: …\"): do NOT call " +
        "todos_add to duplicate plan steps — work directly against the pre-seeded items and call todos_complete " +
        "on each step as soon as its deliverable is written and verified (you may call todos_complete in parallel " +
        "with the next step's first tool call, or complete multiple step IDs at once if a single change satisfies them; " +
        "never complete a step on intent alone). Reuse the findings and tool results already gathered during Plan mode " +
        "instead of re-running redundant searches, and write target deliverables directly without creating unrequested " +
        "scratch files. When all steps are complete, reply with a concise summary of what was delivered. " +
        SubmitPlanUnavailableLine;

    /// <summary>首跑用户消息：只做触发，计划全文与用户原始诉求由每轮注入的交接上下文携带（单一副本）。</summary>
    public static string BuildTriggerInstruction(ApprovedPlanSnapshot snapshot) =>
        $"Execute approved plan {snapshot.PlanId} revision {snapshot.Revision} now. " +
        "The full plan and the user's original request are in the system context.";

    /// <summary>
    /// 交接上下文：计划正文只写「做什么」——用户原话、规划阶段的澄清问答与修订反馈随快照冻结并在每轮注入，
    /// 确保跨上下文压缩或进程恢复时硬约束不丢失。
    /// </summary>
    public static string BuildHandoffContext(ApprovedPlanSnapshot snapshot)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.OriginalRequest))
            sections.Add($"## USER'S ORIGINAL REQUEST\n{snapshot.OriginalRequest}");
        if (snapshot.Clarifications.Count > 0)
        {
            sections.Add("## CLARIFICATIONS AND FEEDBACK DURING PLANNING\n"
                + string.Join("\n", snapshot.Clarifications.Select(line => $"- {line}")));
        }

        var steps = string.Join("\n", snapshot.Steps.Select(step =>
            $"- {step.Id}: {step.Title}\n  {step.Description}\n  Acceptance: {string.Join("; ", step.AcceptanceCriteria)}"));
        sections.Add($"""
            ## IMMUTABLE APPROVED PLAN
            Plan: {snapshot.PlanId} · Revision: {snapshot.Revision} · Content hash: {snapshot.ContentHash}

            {snapshot.Markdown}

            ## STRUCTURED STEPS
            {steps}
            """);
        sections.Add($"## EXECUTION RULES\n{ExecutionRules}");
        return string.Join("\n\n", sections);
    }
}
