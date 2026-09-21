namespace OneCode.Core.Domain;

using Microsoft.Extensions.AI;
using OneCode.Core.Models;

public enum ThinkingMode
{
    Disabled,
    Enabled,
    Adaptive,
}

public enum EffortLevel
{
    Low,
    Medium,
    High,
    Max,
}

public static class EffortThinking
{
    public static EffortLevel ParseEffort(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => EffortLevel.Low,
        "medium" => EffortLevel.Medium,
        "high" => EffortLevel.High,
        "max" => EffortLevel.Max,
        _ => EffortLevel.Medium,
    };

    /// <summary>
    /// 优先使用 <paramref name="model"/> 中的 catalog ThinkingBudget 作为基础值；
    /// 缺省时回退到模型名称启发式。
    /// </summary>
    public static int GetThinkingBudget(EffortLevel effort, string modelId, int? maxAllowed = null, ModelInfo? model = null)
    {
        var baseBudget = model?.ThinkingBudget ?? GetBaseBudget(modelId);

        var budget = effort switch
        {
            EffortLevel.Low => (int)(baseBudget * 0.25),
            EffortLevel.Medium => (int)(baseBudget * 0.5),
            EffortLevel.High => baseBudget,
            EffortLevel.Max => (int)(baseBudget * 2),
            _ => baseBudget,
        };

        return maxAllowed.HasValue ? Math.Min(budget, maxAllowed.Value) : budget;
    }

    // 仅单元测试使用：生产代码当前无调用方（测试接缝）。
    public static bool ShouldEnableThinking(ThinkingMode mode, EffortLevel effort)
    {
        return mode switch
        {
            ThinkingMode.Disabled => false,
            ThinkingMode.Enabled => true,
            ThinkingMode.Adaptive => effort >= EffortLevel.Medium,
            _ => false,
        };
    }

    /// <summary>
    /// 把产品侧的 token budget 映射为 MEAI 标准 <see cref="ReasoningEffort"/>。
    ///
    /// MEAI 只提供离散档位，各 provider 适配器再把它翻译成自家参数
    /// （Anthropic: 1024/8192/16384/32768；OpenAI: reasoning_effort 字符串）。
    /// 取「不小于请求 budget 的最小档位」，保证实际思考预算不会低于用户设定。
    /// </summary>
    public static ReasoningEffort ToReasoningEffort(int budgetTokens) => budgetTokens switch
    {
        <= 0 => ReasoningEffort.None,
        <= 1024 => ReasoningEffort.Low,
        <= 8192 => ReasoningEffort.Medium,
        <= 16384 => ReasoningEffort.High,
        _ => ReasoningEffort.ExtraHigh,
    };

    /// <summary>启发式基础 budget——仅在 catalog 无数据时使用。</summary>
    private static int GetBaseBudget(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return 10000;

        if (modelId.Contains("opus", StringComparison.OrdinalIgnoreCase)) return 32000;
        if (modelId.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return 16000;
        return 10000;
    }
}
