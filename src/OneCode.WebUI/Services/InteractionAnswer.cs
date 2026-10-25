using OneCode.Core.Goals;
using OneCode.Core.Permissions;

namespace OneCode.WebUI.Services;

/// <summary>中断卡片的一次应答（只填当前卡片对应的字段）。</summary>
public sealed record InteractionAnswer(
    ApprovalDecision? Approval = null,
    string? Text = null,
    GoalBudgetDecision? Budget = null);
