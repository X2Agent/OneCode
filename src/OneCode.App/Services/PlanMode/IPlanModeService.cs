namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 模式运行时服务：只负责模式状态、提示词和权限同步。
/// 持久化计划、审批与执行状态由 <see cref="IPlanWorkflowApplicationService"/> 负责。
/// </summary>
public interface IPlanModeService
{
    /// <summary>当前是否处于 Plan 模式。</summary>
    bool IsInPlanMode { get; }

    /// <summary>进入 Plan 模式之前的权限模式（用于 Exit 时恢复）。</summary>
    PermissionMode? PrePlanMode { get; }

    /// <summary>
    /// 进入 Plan 模式：保存上一权限模式，并将 <see cref="IPermissionModeProvider"/> 设为 Plan。
    /// </summary>
    void EnterPlanMode();

    /// <summary>
    /// 退出 Plan 模式：恢复 <see cref="PrePlanMode"/>（缺省 Default），并同步权限 provider。
    /// </summary>
    /// <returns>恢复后的权限模式。</returns>
    PermissionMode ExitPlanMode();

    /// <summary>
    /// 获取当前 Plan 模式的完整工作流指令（从 prompt 文件加载，带缓存）。
    /// 不改变 Plan 模式状态，仅用于注入 system prompt。
    /// </summary>
    Task<string> GetWorkflowInstructionsAsync(CancellationToken ct);
}
