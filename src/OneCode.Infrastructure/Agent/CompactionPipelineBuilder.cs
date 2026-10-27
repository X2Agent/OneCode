#pragma warning disable MAAI001 // MAF 实验性 API（压缩策略族）：升级 MAF 时按升级复查清单回归
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Ai;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 为主 Agent 与子 Agent 构建 L1/L2/L3 压缩策略链。
/// 策略经 <c>HarnessAgentOptions.CompactionStrategy</c> 交给 Harness；产品侧再挂一份
/// <c>CompactionProvider</c> 会让同一次模型调用被压缩两次。
/// 不按 <c>(toolName, args)</c> 去重：相同调用在重试成功或文件被外部修改后结果可能不同。
/// 阈值按模型上下文窗口比例计算，非法配置在装配时抛异常。
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

    // 只读调研档（Explore / Plan 子代理）的绝对阈值上限。比例阈值在 1M 窗口上会放任单个调研子代理
    // 把上下文涨到数十万 token：每轮请求都重复付费这份前缀，而调研结果的价值随轮次递减。
    // 实际阈值取 min(比例阈值, 绝对上限)，小窗口模型仍按比例走。
    private const int ResearchToolEvictionCapTokens = 40_000;
    private const int ResearchSummarizationCapTokens = 80_000;
    private const int ResearchTruncationCapTokens = 120_000;

    /// <summary>
    /// 从输入预算中预留、用于指令、工具 schema 与协议封装的比例。
    /// </summary>
    /// <remarks>
    /// 用比例而非固定值：这部分开销随工具面与系统提示词增长，而两者都随模型预算缩放。
    /// 取 10% 是刻意保守的估计——目的是避免阈值按实际不可用的空间计算，而不是精确建模请求开销。
    /// </remarks>
    private const double RequestOverheadRatio = 0.10;

    /// <summary>
    /// 为主 Agent 构建标准压缩策略；阈值按模型上下文窗口比例计算，自动适配不同模型。
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
    /// Builds the standard compaction strategy for worker agents (sub-agents).
    /// Worker 的阈值比 Main 更激进，因为 sub-agent 上下文更短、生命周期更短。
    /// <paramref name="readOnlyResearch"/> 为 true（Explore / Plan）时再叠加绝对 token 上限。
    /// </summary>
    public static CompactionStrategy BuildForWorkerAgent(
        IChatClient chatClient,
        int maxContextWindowTokens,
        int maxOutputTokens,
        string? summarizationPrompt = null,
        bool readOnlyResearch = false)
    {
        return BuildPipeline(
            chatClient,
            maxContextWindowTokens,
            maxOutputTokens,
            WorkerToolEvictionRatio,
            WorkerSummarizationRatio,
            WorkerTruncationRatio,
            summarizationPrompt,
            readOnlyResearch
                ? (ResearchToolEvictionCapTokens, ResearchSummarizationCapTokens, ResearchTruncationCapTokens)
                : null);
    }

    private static CompactionStrategy BuildPipeline(
        IChatClient chatClient,
        int maxContextWindowTokens,
        int maxOutputTokens,
        double toolEvictionRatio,
        double summarizationRatio,
        double truncationRatio,
        string? summarizationPrompt,
        (int ToolEviction, int Summarization, int Truncation)? absoluteCaps = null)
    {
        var inputBudget = ResolveInputBudget(maxContextWindowTokens, maxOutputTokens);

        var toolEvictionTokens = Capped((int)(inputBudget * toolEvictionRatio), absoluteCaps?.ToolEviction);
        var summarizationTokens = Capped((int)(inputBudget * summarizationRatio), absoluteCaps?.Summarization);
        var truncationTokens = Capped((int)(inputBudget * truncationRatio), absoluteCaps?.Truncation);

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

    private static int Capped(int ratioThreshold, int? cap) => cap is { } limit ? Math.Min(ratioThreshold, limit) : ratioThreshold;

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
