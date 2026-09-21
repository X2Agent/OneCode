using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Ai;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Builds the compaction <b>strategy</b> for main and worker agents.
///
/// <para><b>策略组成</b>：L1 折叠旧 tool call 组 → L2 LLM 摘要 → L3 截断。
/// 三层均为 MAF 原生策略，阈值按模型上下文窗口比例计算，替代早期版本的硬编码绝对值阈值。
/// 所有策略由 <see cref="PipelineCompactionStrategy"/> 串联执行。</para>
///
/// <para><b>注入方式</b>：策略经 <c>HarnessAgentOptions.CompactionStrategy</c> 交给 Harness，
/// 由 Harness 自行挂载 <c>CompactionProvider</c>。产品侧不再单独构建 provider——
/// 两处各挂一份会造成同一次模型调用被压缩两次，而外层一份会覆盖内层的状态。</para>
///
/// <para><b>不按调用参数去重</b>：曾有一层按 <c>(toolName, args)</c> 剔除「重复」调用组。
/// 该判定不等价——同名同参数的调用完全可能产生不同结果（失败→成功、文件已被修改），
/// 因此该层已移除；需要压缩时由 L1 折叠 / L2 摘要 / L3 截断按预算处理，
/// 而不是把非等价调用当作重复丢弃。</para>
///
/// <para><b>策略管道（按 escalation 顺序）</b>：
/// <list type="number">
///   <item><description><see cref="ToolResultCompactionStrategy"/> — 折叠旧 tool call 组为 YAML 摘要</description></item>
///   <item><description><see cref="SummarizationCompactionStrategy"/> — LLM 深度摘要</description></item>
///   <item><description><see cref="TruncationCompactionStrategy"/> — 兜底截断最旧的非系统消息组</description></item>
/// </list>
/// </para>
///
/// <para><b>阈值计算</b>：所有阈值基于
/// <c>inputBudget = (maxContextWindowTokens - maxOutputTokens) × (1 - RequestOverheadRatio)</c> 按比例计算，
/// 自动适配不同模型的上下文长度（32K ~ 1M+）。非法配置在装配时抛异常，不静默钳制。</para>
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
    /// Fraction of the input budget held back for instructions, tool schemas and protocol framing.
    /// </summary>
    /// <remarks>
    /// A ratio rather than a constant: the overhead grows with the tool surface and the system prompt,
    /// both of which scale with the model's own budget. 10% is deliberately modest — the aim is to stop
    /// the thresholds from being computed against space that is not actually available, not to model the
    /// request precisely.
    /// </remarks>
    private const double RequestOverheadRatio = 0.10;

    /// <summary>
    /// Builds the standard compaction strategy for the main agent.
    /// 阈值按模型上下文窗口比例计算，自动适配不同模型。
    /// </summary>
    /// <param name="chatClient">用于 LLM 摘要的 IChatClient（通常用主模型）。</param>
    /// <param name="maxContextWindowTokens">模型上下文窗口大小（如 1_000_000 for Claude Sonnet 4.5）。</param>
    /// <param name="maxOutputTokens">模型最大输出 token 数。</param>
    /// <param name="summarizationPrompt">自定义摘要 prompt（null 时用 MAF 默认 prompt）。</param>
    public static CompactionStrategy BuildForMainAgent(
        IChatClient chatClient,
        int maxContextWindowTokens,
        int maxOutputTokens,
        string? summarizationPrompt = null)
    {
        return BuildPipeline(
            chatClient,
            maxContextWindowTokens,
            maxOutputTokens,
            MainToolEvictionRatio,
            MainSummarizationRatio,
            MainTruncationRatio,
            summarizationPrompt);
    }

    /// <summary>
    /// Builds the standard compaction strategy for worker agents (sub-agents / team members).
    /// Worker 的阈值比 Main 更激进，因为 sub-agent 上下文更短、生命周期更短。
    /// </summary>
    public static CompactionStrategy BuildForWorkerAgent(
        IChatClient chatClient,
        int maxContextWindowTokens,
        int maxOutputTokens,
        string? summarizationPrompt = null)
    {
        return BuildPipeline(
            chatClient,
            maxContextWindowTokens,
            maxOutputTokens,
            WorkerToolEvictionRatio,
            WorkerSummarizationRatio,
            WorkerTruncationRatio,
            summarizationPrompt);
    }

    private static CompactionStrategy BuildPipeline(
        IChatClient chatClient,
        int maxContextWindowTokens,
        int maxOutputTokens,
        double toolEvictionRatio,
        double summarizationRatio,
        double truncationRatio,
        string? summarizationPrompt)
    {
        var inputBudget = ResolveInputBudget(maxContextWindowTokens, maxOutputTokens);

        var toolEvictionTokens = (int)(inputBudget * toolEvictionRatio);
        var summarizationTokens = (int)(inputBudget * summarizationRatio);
        var truncationTokens = (int)(inputBudget * truncationRatio);

        // L2 的触发条件同时交给原生策略与守卫适配器：守卫必须在与内部策略完全相同的条件下
        // 决定是否放行，否则会出现「守卫以为不该跑、内部却已经改了索引」的分叉。
        var summarizationTrigger = CompactionTriggers.TokensExceed(summarizationTokens);

        var strategy = new PipelineCompactionStrategy(
            // L1: 折叠旧 tool call 组为 YAML 摘要
            // 用产品 formatter 而非 MAF 默认：默认只保留工具名与结果正文，丢掉调用参数
            // （哪次 Read/Grep 覆盖了哪个文件或模式），且结果正文无长度上限。
            // 折叠本身仍是 MAF 原生策略，只替换输出格式。
            new ToolResultCompactionStrategy(
                trigger: CompactionTriggers.TokensExceed(toolEvictionTokens),
                minimumPreservedGroups: 2)
            {
                ToolCallFormatter = OneCodeToolCallFormatter.Format,
            },

            // L2: LLM 深度摘要（自动路径的摘要能力；显式 /compact 仍走 App 层 CompactService）
            // MAF 自带：LLM 调用失败时自动恢复 excluded groups、保留最近 N 组硬下限。
            // 外层守卫补上原生未覆盖的两条路径：空摘要与「摘要比原文更长」不提交，
            // 索引回滚到调用前状态。摘要客户端包一层输出上限，使自动路径与显式
            // /compact 产出同等规模的摘要（MAF 不传 ChatOptions，不包就完全依赖提供方默认值）。
            new GuardedSummarizationCompactionStrategy(
                new SummarizationCompactionStrategy(
                    new SummarizationOutputLimitChatClient(chatClient, SummarizationDefaults.MaxOutputTokens),
                    trigger: summarizationTrigger,
                    minimumPreservedGroups: 8,
                    summarizationPrompt: summarizationPrompt),
                trigger: summarizationTrigger),

            // L3: 兜底截断——移除最旧的非系统消息组
            new TruncationCompactionStrategy(
                trigger: CompactionTriggers.TokensExceed(truncationTokens),
                minimumPreservedGroups: 2));

        return strategy;
    }

    /// <summary>
    /// Derives the message budget from the model's context-window / output-token pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validates the <b>pair</b>, not the individual values: a model with a large context window
    /// legitimately has a large output allowance, and only their relationship is meaningful.
    /// </para>
    /// <para>
    /// A non-positive or inverted pair previously collapsed to a budget of 1 through
    /// <c>Math.Max(1, …)</c>, which hid a configuration error behind thresholds that could never be
    /// met: compaction looked "armed" while no strategy could ever reach its trigger. Rejecting the
    /// pair makes the misconfiguration visible at pipeline build time. MAF applies the same constraint
    /// in <see cref="ContextWindowCompactionStrategy"/>, so this cannot silently diverge from it.
    /// </para>
    /// <para>
    /// The 10% reservation covers what the message index does not count — instructions, tool schemas
    /// and protocol framing. Without it the thresholds are computed against space the messages never
    /// actually get, so they fire later than the real limit.
    /// </para>
    /// </remarks>
    private static int ResolveInputBudget(int maxContextWindowTokens, int maxOutputTokens)
    {
        if (maxContextWindowTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxContextWindowTokens), maxContextWindowTokens,
                "Model context window must be positive.");
        }

        if (maxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens), maxOutputTokens,
                "Reserved output tokens must be positive.");
        }

        if (maxOutputTokens >= maxContextWindowTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens), maxOutputTokens,
                $"Reserved output tokens ({maxOutputTokens}) leave no input budget in a context window " +
                $"of {maxContextWindowTokens}. Check the model's context-window and output-token settings.");
        }

        var reserved = (int)((maxContextWindowTokens - maxOutputTokens) * RequestOverheadRatio);
        return maxContextWindowTokens - maxOutputTokens - reserved;
    }
}
