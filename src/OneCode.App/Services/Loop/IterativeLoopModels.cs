namespace OneCode.App.Services.Loop;

/// <summary>
/// <c>/loop</c> 运行时循环的请求：任务 + 确定性检查 + 硬上限。
/// </summary>
/// <param name="Task">要反复执行的任务描述（fresh context 下每轮原样重放）。</param>
/// <param name="CheckCommand">
/// 确定性检查命令，退出码 0 视为本轮通过。为空时退化为 <see cref="Core.Tools.IVerificationProvider"/>；
/// 两者都不可用时 fail-closed——循环永远不可能通过，跑满上限后判失败。
/// </param>
/// <param name="MaxIterations">调用 inner agent 的硬上限（对应 <c>LoopAgentOptions.MaxIterations</c>）。</param>
public sealed record LoopRunRequest(
    string Task,
    string? CheckCommand,
    int MaxIterations);

/// <summary>整个循环的产物：是否通过、真实调用轮数、汇总与累计 token。</summary>
public sealed record LoopRunOutcome(
    bool Completed,
    int Attempts,
    string Summary,
    long InputTokens,
    long OutputTokens);
