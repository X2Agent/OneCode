using System.Text;
using OneCode.Core.Coordinator;
using OneCode.Core.IO;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 子目标硬校验与汇总的纯函数集：越界文件检测、工作区路径解析、judge 证据渲染、执行汇总文本。
/// 全部为静态函数（record/标量入出），无 I/O 无状态——越界判定规则与汇总格式的回归由此锚定。
/// </summary>
internal static class GoalSubGoalAssessment
{
    /// <summary>找出超出工作区与 allowedPaths 白名单的变更文件（越界即硬校验失败）。</summary>
    public static IReadOnlyList<string> FindOutOfScopeFiles(
        string workingDirectory,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> allowedPaths)
    {
        var allowedRoots = allowedPaths
            .Select(path => ResolveWorkspacePath(workingDirectory, path))
            .ToList();
        return changedFiles
            .Select(Path.GetFullPath)
            .Where(path => !PathBoundary.IsWithinDirectory(path, workingDirectory)
                || allowedRoots.Count > 0 && !allowedRoots.Any(root => PathBoundary.IsWithinDirectory(path, root)))
            .Select(path => Path.GetRelativePath(workingDirectory, path))
            .ToList();
    }

    public static string ResolveWorkspacePath(string workingDirectory, string path)
    {
        var resolved = Path.GetFullPath(path, workingDirectory);
        if (!PathBoundary.IsWithinDirectory(resolved, workingDirectory))
            throw new InvalidOperationException($"Declared Goal path '{path}' is outside the working directory.");
        return resolved;
    }

    public static string FormatEvidenceForJudge(SubGoalEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Agent summary:");
        builder.AppendLine(evidence.AgentSummary);
        builder.AppendLine(CultureInfo.InvariantCulture, $"Changed files: {(evidence.ChangedFiles.Count == 0 ? "(none)" : string.Join(", ", evidence.ChangedFiles))}");
        builder.AppendLine("Deterministic validation:");
        foreach (var validation in evidence.Validations)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {validation.Gate}: {(validation.Skipped ? "SKIPPED" : validation.Passed ? "PASSED" : "FAILED")} — {validation.Summary}");
        return builder.ToString();
    }

    public static string TruncateForSummary(string output, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(output)) return "";
        if (output.Length <= maxChars) return output;
        return output[..maxChars] + "...";
    }

    /// <summary>生成 GOAL 模式执行汇总文本。</summary>
    public static string BuildSummary(
        GoalPlan plan,
        IReadOnlyList<SubGoalExecution> executions,
        long totalInputTokens,
        long totalOutputTokens,
        bool usedFallback)
    {
        var completed = plan.Goals.Where(g => g.Status == GoalStatus.Completed).ToList();
        var failed = plan.Goals.Where(g => g.Status == GoalStatus.Failed).ToList();
        var skipped = plan.Goals.Where(g => g.Status == GoalStatus.Skipped).ToList();

        var lines = new List<string>
        {
            "",
            "═══════════════════════════════════════",
            "  GOAL MODE EXECUTION SUMMARY",
            "═══════════════════════════════════════",
            "",
        };

        if (usedFallback)
            lines.Add("  (Decomposition failed — executed as single goal)");

        lines.Add($"  Completed: {completed.Count}/{plan.Goals.Count}");
        lines.Add($"  Failed:    {failed.Count}/{plan.Goals.Count}");

        if (skipped.Count > 0)
            lines.Add($"  Skipped:   {skipped.Count}/{plan.Goals.Count}");

        lines.Add("");
        lines.Add($"  Total attempts:   {executions.Sum(e => e.Attempts)}");
        lines.Add($"  Input tokens:     {totalInputTokens:N0}");
        lines.Add($"  Output tokens:    {totalOutputTokens:N0}");

        if (executions.Count > 1)
        {
            lines.Add("");
            lines.Add("  Per-sub-goal token usage:");
            foreach (var exec in executions)
            {
                var goalDesc = plan.Goals.FirstOrDefault(g => g.Id == exec.GoalId)?.Description ?? "(unknown)";
                if (goalDesc.Length > 40) goalDesc = goalDesc[..37] + "...";
                lines.Add($"    #{exec.GoalId} ({exec.Status}, {exec.Attempts} attempts): {exec.InputTokens + exec.OutputTokens:N0} tokens — {goalDesc}");
            }
        }

        if (completed.Count > 0)
        {
            lines.Add("");
            lines.Add("  Completed sub-goals:");
            foreach (var g in completed)
                lines.Add($"    ✓ #{g.Id}: {g.Description}");
        }

        if (failed.Count > 0)
        {
            lines.Add("");
            lines.Add("  Failed sub-goals:");
            foreach (var g in failed)
            {
                var exec = executions.FirstOrDefault(e => e.GoalId == g.Id);
                lines.Add($"    ✗ #{g.Id}: {g.Description}");
                if (exec is not null && !string.IsNullOrEmpty(exec.Evaluation))
                    lines.Add($"      → {exec.Evaluation}");
            }
        }

        if (skipped.Count > 0)
        {
            lines.Add("");
            lines.Add("  Skipped sub-goals (due to iteration limit or prior failures):");
            foreach (var g in skipped)
                lines.Add($"    - #{g.Id}: {g.Description}");
        }

        lines.Add("");
        lines.Add("═══════════════════════════════════════");

        return string.Join("\n", lines);
    }

    /// <summary>从编排事件流捕获工具执行证据。</summary>
    public static void CaptureToolEvidence(
        OrchestrationEvent evt,
        ICollection<GoalToolExecutionEvidence> toolExecutions)
    {
        if (evt is OrchestrationEvent.ToolDone done)
            toolExecutions.Add(new GoalToolExecutionEvidence(done.Name, done.IsError, done.Result));
    }
}
