using System.Text;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 整合提示词构建器：收集上次整合以来写入的 working-memory 快照作为主要候选来源，
/// 渲染 <c>system/autodream-consolidation</c> 模板变量，产出整合 Agent 的完整提示词。
/// </summary>
internal sealed class AutoDreamConsolidationPromptBuilder(
    IPromptManager promptManager,
    AutoDreamWorkingMemoryScanner workingMemoryScanner,
    Func<string?> projectRootProvider)
{
    private readonly IPromptManager _promptManager = promptManager;
    private readonly AutoDreamWorkingMemoryScanner _workingMemoryScanner = workingMemoryScanner;
    private readonly Func<string?> _projectRootProvider = projectRootProvider;

    /// <summary>
    /// Upper bound on working-memory artefacts included in one consolidation prompt.
    /// Working notes are unbounded in principle; the prompt is not.
    /// </summary>
    private const int MaxWorkingMemoryArtefacts = 20;

    /// <summary>构建整合提示词：注入自 <paramref name="since"/> 以来的 working-memory 快照与新会话数。</summary>
    public async Task<string> BuildConsolidationPromptAsync(DateTimeOffset since, int sessionCount, CancellationToken ct)
    {
        var projectRoot = _projectRootProvider() ?? "(unknown)";

        // Working-memory artefacts are the primary candidate source: they hold what the agent wrote
        // down while working. Session events stay available for verification, but they only record
        // that a conversation happened.
        var artefacts = _workingMemoryScanner.Collect(projectRoot, since, MaxWorkingMemoryArtefacts, ct);
        var variables = new Dictionary<string, string>
        {
            ["since"] = since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["session_count"] = sessionCount.ToString(CultureInfo.InvariantCulture),
            ["project_root"] = projectRoot,
            ["working_memory_root"] = FileMemoryStorePaths.ResolveRoot(projectRoot),
            ["working_memory_section"] = FormatWorkingMemorySection(artefacts),
        };
        return await _promptManager.RenderPromptAsync("system/autodream-consolidation", variables, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Renders the snapshot as prompt text. The path and timestamp are included so every candidate can
    /// be attributed back to the revision it came from.
    /// </summary>
    private static string FormatWorkingMemorySection(IReadOnlyList<WorkingMemoryArtefact> artefacts)
    {
        if (artefacts.Count == 0)
        {
            return "No working-memory artefacts were written since the last consolidation. " +
                   "Fall back to reviewing the recent sessions below.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{artefacts.Count} artefact(s) were written since the last consolidation:");

        foreach (var artefact in artefacts)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"### {artefact.RelativePath} (modified {artefact.LastModifiedAt:yyyy-MM-dd HH:mm:ss}Z)");
            sb.AppendLine(artefact.Content.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }
}
