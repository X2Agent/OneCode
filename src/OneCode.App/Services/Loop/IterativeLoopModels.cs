using OneCode.Core.Config;

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

/// <summary>单轮迭代的验证结果，用于进度投影与下一轮反馈。</summary>
public sealed record LoopIterationReport(
    int Iteration,
    int MaxIterations,
    bool Passed,
    string Feedback);

/// <summary>整个循环的产物：是否通过、真实调用轮数、汇总与累计 token。</summary>
public sealed record LoopRunOutcome(
    bool Completed,
    int Attempts,
    string Summary,
    long InputTokens,
    long OutputTokens);

/// <summary>循环上限的单一来源（配置键 <c>loop.maxIterations</c>）。</summary>
internal static class LoopDefaults
{
    /// <summary>默认硬上限；相比 MAF <c>LoopAgent</c> 的默认 10 刻意收紧（每轮是一次完整 agent run）。</summary>
    public const int MaxIterationsFallback = 3;

    public static int ResolveMaxIterations(ModeBudgetSettings? settings)
        => settings is null ? MaxIterationsFallback : settings.MaxLoopIterations;
}
