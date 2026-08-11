namespace OneCode.Core.Domain;

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
    /// 计算 thinking budget。
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

    /// <summary>启发式基础 budget——仅在 catalog 无数据时使用。</summary>
    private static int GetBaseBudget(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return 10000;

        if (modelId.Contains("opus", StringComparison.OrdinalIgnoreCase)) return 32000;
        if (modelId.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return 16000;
        return 10000;
    }
}
