using OneCode.Infrastructure.Config;
using IAppStateAccessor = OneCode.Core.Domain.IAppStateAccessor;

namespace OneCode.App.Services;

/// <summary>
/// Resolves the effective thinking (extended reasoning) parameters for a given LLM call.
///
/// 两个独立维度（由 <see cref="OneCode.App.Commands.ThinkCommand"/> 管理）：
///   1. <see cref="OneCode.Core.Domain.AppState.ThinkingEnabled"/> — 模型思考开关
///   2. <see cref="OneCode.Core.Domain.AppState.EffortValue"/> — reasoning_effort 努力程度
///
/// 启用规则：ThinkingEnabled=true 时启用扩展思考，budget 由 EffortValue 决定。
/// 返回 (Enable, BudgetTokens)：调用方将 BudgetTokens 传给 StreamQueryAsync。
/// </summary>
public sealed class ThinkingParamsResolver(IConfigManager configManager)
{
    public (bool Enable, int BudgetTokens) Resolve(IAppStateAccessor appState, string modelId)
    {
        var state = appState.Current;

        if (!state.ThinkingEnabled)
            return (false, 0);

        var budget = EffortThinking.GetThinkingBudget(state.EffortValue, modelId);
        return (true, budget);
    }
}
