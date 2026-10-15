using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// BuildRun 终态到计划工作流失败语义的单一映射（派发器的流末收口与恢复扫描的对账共用）。
/// 计划成败只由 BuildRun 终态决定：<see cref="BuildRunState.Completed"/> 即计划完成，
/// 其余终态按此处的错误码与文案落为工作流失败。
/// </summary>
internal static class BuildRunClosure
{
    /// <summary>
    /// 非 Completed 收口的错误码。Blocked 单独成码：产物已保留待人工复核，与「执行失败」
    /// 的处置路径不同，TUI 与恢复逻辑都需要区分。
    /// </summary>
    public static string FailureCode(BuildRunState state) => state switch
    {
        BuildRunState.Blocked => "BuildRunPendingReview",
        _ when BuildStateTransitionService.IsTerminal(state) => $"BuildRun{state}",
        _ => "BuildRunIncomplete",
    };

    public static string FailureMessage(BuildRunState state, string? summary) => state switch
    {
        BuildRunState.Blocked =>
            "Build output was kept for your review. " + (summary ?? "The BuildRun is blocked."),
        _ when BuildStateTransitionService.IsTerminal(state) =>
            summary ?? $"The BuildRun terminated in state '{state}'.",
        _ => $"The BuildRun stopped in non-terminal state '{state}' before completing.",
    };
}
