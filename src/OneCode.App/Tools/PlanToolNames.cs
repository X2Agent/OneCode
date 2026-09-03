namespace OneCode.App.Tools;

/// <summary>
/// Plan 执行工具的注册名——跨文件契约键。AddTool 注册、工具调用记录特判、
/// 协议提示文本一律引用此处常量；改名必须同步协议语义相关代码，
/// 禁止在调用点重新内联字面量。
/// </summary>
public static class PlanToolNames
{
    /// <summary>更新计划步骤执行状态（<see cref="PlanExecutionTool.UpdatePlanStepAsync"/>）。</summary>
    public const string UpdateStep = "UpdatePlanStep";

    /// <summary>提交计划验证证据（<see cref="PlanExecutionTool.CompletePlanVerificationAsync"/>）。</summary>
    public const string CompleteVerification = "CompletePlanVerification";
}