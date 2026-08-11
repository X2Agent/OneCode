using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Builds MAF compaction providers for main and worker agents.
///
/// <para><b>设计理念</b>：完全使用 MAF 原生压缩策略，按模型上下文窗口比例计算阈值，
/// 替代早期版本的硬编码绝对值阈值。所有策略由 <see cref="PipelineCompactionStrategy"/> 串联执行。</para>
///
/// <para><b>策略管道（按 escalation 顺序）</b>：
/// <list type="number">
///   <item><description><see cref="ToolResultCompactionStrategy"/> — 折叠旧 tool call 组为 YAML 摘要（替代 MicroCompactService）</description></item>
///   <item><description><see cref="SummarizationCompactionStrategy"/> — LLM 深度摘要（替代 CompactService 的核心摘要能力）</description></item>
///   <item><description><see cref="TruncationCompactionStrategy"/> — 兜底截断最旧的非系统消息组</description></item>
/// </list>
/// </para>
///
/// <para><b>阈值计算</b>：所有阈值基于 <c>inputBudget = maxContextWindowTokens - maxOutputTokens</c> 按比例计算，
/// 自动适配不同模型的上下文长度（32K ~ 1M+）。</para>
///
/// <para><b>CompactionProvider 必须通过 IChatClient builder 层注入</b>（<c>AsBuilder().UseAIContextProviders(...)</c>），
/// 而非作为 agent-level context provider。压缩状态自动持久化在 <see cref="Microsoft.Agents.AI.AgentSession"/>.StateBag 中，
/// 由 <c>AgentSessionStore.cs</c> 的 mafSession 持久化机制携带跨进程恢复。</para>
/// </summary>
public static class CompactionPipelineBuilder
{
    // Main Agent 阈值比例
    private const double MainToolEvictionRatio = 0.5;   // 50% input budget 触发 tool result 折叠
    private const double MainSummarizationRatio = 0.7;  // 70% 触发 LLM 摘要
    private const double MainTruncationRatio = 0.85;    // 85% 触发截断兜底

    // Worker Agent 阈值比例（更激进，因上下文更短）
    private const double WorkerToolEvictionRatio = 0.4;
    private const double WorkerSummarizationRatio = 0.6;
    private const double WorkerTruncationRatio = 0.8;

    /// <summary>
    /// Builds the standard compaction pipeline for the main agent.
    /// 阈值按模型上下文窗口比例计算，自动适配不同模型。
    /// </summary>
    /// <param name="chatClient">用于 LLM 摘要的 IChatClient（通常用主模型）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="maxContextWindowTokens">模型上下文窗口大小（如 1_000_000 for Claude Sonnet 4.5）。</param>
    /// <param name="maxOutputTokens">模型最大输出 token 数。</param>
    /// <param name="summarizationPrompt">自定义摘要 prompt（null 时用 MAF 默认 prompt）。</param>
    public static CompactionProvider BuildForMainAgent(
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        int maxContextWindowTokens,
        int maxOutputTokens,
        string? summarizationPrompt = null)
    {
        return BuildPipeline(
            chatClient,
            loggerFactory,
            maxContextWindowTokens,
            maxOutputTokens,
            MainToolEvictionRatio,
            MainSummarizationRatio,
            MainTruncationRatio,
            summarizationPrompt);
    }

    /// <summary>
    /// Builds the standard compaction pipeline for worker agents (sub-agents / team members).
    /// Worker 的阈值比 Main 更激进，因为 sub-agent 上下文更短、生命周期更短。
    /// </summary>
    public static CompactionProvider BuildForWorkerAgent(
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        int maxContextWindowTokens,
        int maxOutputTokens,
        string? summarizationPrompt = null)
    {
        return BuildPipeline(
            chatClient,
            loggerFactory,
            maxContextWindowTokens,
            maxOutputTokens,
            WorkerToolEvictionRatio,
            WorkerSummarizationRatio,
            WorkerTruncationRatio,
            summarizationPrompt);
    }

    private static CompactionProvider BuildPipeline(
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        int maxContextWindowTokens,
        int maxOutputTokens,
        double toolEvictionRatio,
        double summarizationRatio,
        double truncationRatio,
        string? summarizationPrompt)
    {
        var inputBudget = Math.Max(1, maxContextWindowTokens - maxOutputTokens);

        var toolEvictionTokens = (int)(inputBudget * toolEvictionRatio);
        var summarizationTokens = (int)(inputBudget * summarizationRatio);
        var truncationTokens = (int)(inputBudget * truncationRatio);

        var strategy = new PipelineCompactionStrategy(
            // L0: 去重——移除重复的 (toolName, args) 调用组，只保留最近一次（项目自定义策略）
            // 轻量内存操作（无 LLM），在 token 阈值触发前先清理无意义的重复调用
            new SnipDuplicateCallsCompactionStrategy(),

            // L1: 折叠旧 tool call 组为 YAML 摘要（替代 MicroCompactService）
            // MAF 的折叠比项目的"清空内容"更优——保留工具名和返回值摘要
            new ToolResultCompactionStrategy(
                trigger: CompactionTriggers.TokensExceed(toolEvictionTokens),
                minimumPreservedGroups: 2),

            // L2: LLM 深度摘要（替代 CompactService 的核心摘要能力）
            // MAF 自带：LLM 调用失败时自动恢复 excluded groups、保留最近 N 组硬下限
            new SummarizationCompactionStrategy(
                chatClient,
                trigger: CompactionTriggers.TokensExceed(summarizationTokens),
                minimumPreservedGroups: 8,
                summarizationPrompt: summarizationPrompt),

            // L3: 兜底截断——移除最旧的非系统消息组
            new TruncationCompactionStrategy(
                trigger: CompactionTriggers.TokensExceed(truncationTokens),
                minimumPreservedGroups: 2));

        return new CompactionProvider(strategy, "compaction", loggerFactory);
    }
}
