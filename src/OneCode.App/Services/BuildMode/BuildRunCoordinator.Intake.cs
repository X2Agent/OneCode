using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Build 模式的需求评估管道（BeginOrResume 与恢复路径共用）。
/// 以 partial 拆分：方法强耦合共享 SaveAndReloadAsync/transitions 等私有状态，
/// 符合 App/AGENTS.md 的 partial 规则②；生命周期控制面保留在主文件。
/// </summary>
public sealed partial class BuildRunCoordinator
{
    private async Task<BuildRun> ContinueAssessmentAsync(
        BuildRun current,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null)
    {
        var now = DateTimeOffset.UtcNow;
        var run = current;
        if (run.State == BuildRunState.Intake)
        {
            run = transitions.Transition(run, BuildRunState.Assessing, now);
            run = await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        var assessment = run.Assessment ?? assessmentService.Assess(run.IntakePrompt);
        run = run with { Assessment = assessment };
        return await PrepareForExecutionAsync(
            run,
            CreateScope(run.IntakePrompt, run.Plan is null ? "runtime-derived" : "prescribed-plan", now, run.Plan),
            now,
            ct,
            durableStateObserver,
            run.Plan).ConfigureAwait(false);
    }

    private async Task<BuildRun> PrepareForExecutionAsync(
        BuildRun current,
        BuildScopeSnapshot scope,
        DateTimeOffset now,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null,
        BuildPlan? prescribedPlan = null)
    {
        var run = current;
        if (run.State is BuildRunState.Assessing or BuildRunState.Clarifying)
        {
            run = transitions.Transition(run, BuildRunState.ScopeConfirmed, now) with
            {
                ProposedScope = null,
                Scope = scope,
                ClarificationQuestions = [],
            };
            run = await SaveAndReloadAsync(
                run,
                current.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        if (run.State == BuildRunState.ScopeConfirmed)
        {
            run = transitions.Transition(run, BuildRunState.Planning, now);
            run = await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        if (run.State == BuildRunState.Planning)
        {
            var plan = run.Plan
                ?? prescribedPlan
                ?? CreateQuickFixPlan(scope);
            BuildPlanValidator.Validate(plan);
            plan = taskLinker.LinkPlanTasks(run, plan);
            run = run with { Plan = plan };
            run = transitions.Transition(run, BuildRunState.Planned, now);
            run = await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        // Planned is a terminal park for this method: execution starts only after the plan
        // is approved. 上层批准的预置计划由 BuildPreambleRunner 自动放行（ApprovePlanAsync）。
        return run;
    }
}
