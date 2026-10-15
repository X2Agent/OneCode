namespace OneCode.App.Services.BuildMode;

/// <summary>Plan run 离场方式，决定收口漏斗对计划工作流的处置。</summary>
internal enum PlanRunEnd
{
    /// <summary>run 自行结束（含输出被拦截、运行异常）：已提交的计划进入待审批。</summary>
    Finished,

    /// <summary>用户中止 run：工作流置为已取消。</summary>
    Cancelled,
}
