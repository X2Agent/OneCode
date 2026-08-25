using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Build 模式的需求评估 / 澄清对话管道（BeginOrResume 与恢复路径共用）。
/// 以 partial 拆分：四个方法强耦合共享 SaveAndReloadAsync/transitions 等私有状态，
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
        if (run.Plan is null && assessment.RequiresClarification)
        {
            IReadOnlyList<string> questions;
            try
            {
                questions = (await clarificationGenerator.GenerateAsync(run.IntakePrompt, assessment, ct).ConfigureAwait(false)).Questions;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await BlockClarificationFailureAsync(run, ex, ct, durableStateObserver).ConfigureAwait(false);
            }

            run = transitions.Transition(run, BuildRunState.Clarifying, now) with
            {
                Assessment = assessment,
                ClarificationQuestions = questions,
            };
            return await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        run = run with { Assessment = assessment };
        return await PrepareForExecutionAsync(
            run,
            CreateScope(run.IntakePrompt, run.Plan is null ? "runtime-derived" : "prescribed-plan", now, run.Plan),
            now,
            ct,
            durableStateObserver,
            run.Plan).ConfigureAwait(false);
    }

    private async Task<BuildRun> ContinueClarificationAsync(
        BuildRun current,
        string response,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (current.ProposedScope is not null && IsConfirmation(response))
        {
            var confirmed = current.ProposedScope with
            {
                ConfirmedBy = "user",
                ConfirmedAt = now,
            };
            return await PrepareForExecutionAsync(
                current,
                confirmed,
                now,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        var combined = $"{current.IntakePrompt}\nClarification response: {response.Trim()}";
        var assessment = assessmentService.Assess(combined);
        BuildScopeSnapshot? proposed = null;
        IReadOnlyList<string> questions;
        if (assessment.RequiresClarification)
        {
            try
            {
                questions = (await clarificationGenerator.GenerateAsync(combined, assessment, ct).ConfigureAwait(false)).Questions;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await BlockClarificationFailureAsync(current, ex, ct, durableStateObserver).ConfigureAwait(false);
            }
        }
        else
        {
            proposed = CreateScope(combined, "pending-user-confirmation", now, current.Plan);
            questions = ["开始修改前，请确认建议的任务范围；也可以取消或补充修正。"];
        }

        var updated = current with
        {
            IntakePrompt = combined,
            Assessment = assessment,
            ProposedScope = proposed,
            ClarificationQuestions = questions,
            SequenceNumber = current.SequenceNumber + 1,
            UpdatedAt = now,
        };
        return await SaveAndReloadAsync(updated, current.Version, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fail-closed clarification: the generator refused (model error / no valid questions),
    /// so the run is parked as Blocked instead of falling back to template questions.
    /// </summary>
    private async Task<BuildRun> BlockClarificationFailureAsync(
        BuildRun run,
        Exception error,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null)
    {
        var blocked = transitions.Transition(run, BuildRunState.Blocked, DateTimeOffset.UtcNow) with
        {
            TerminalReason = BuildTerminalReason.Blocked,
            FailureSummary = $"澄清问题生成失败：{error.Message}",
        };
        return await SaveAndReloadAsync(blocked, run.Version, ct, durableStateObserver).ConfigureAwait(false);
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

        // Planned is a terminal park for this method: execution starts only after the user
        // approves the plan + tool policy via ApprovePlanAsync (see IBuildRunCoordinator).
        return run;
    }

    private static bool IsConfirmation(string response) =>
        response.Trim().Equals("confirm", StringComparison.OrdinalIgnoreCase)
        || response.Trim().Equals("confirmed", StringComparison.OrdinalIgnoreCase)
        || response.Trim().Equals("确认", StringComparison.Ordinal)
        || response.Trim().Equals("确认执行", StringComparison.Ordinal);
}