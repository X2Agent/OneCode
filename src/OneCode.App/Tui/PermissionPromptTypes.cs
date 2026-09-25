namespace OneCode.App.Tui;

public sealed record PermissionPromptRequest(
    string Title,
    string Message,
    bool AllowApprovals = true,
    /// <summary>
    /// 是否提供「本次对话全部允许」升级档。Main 路径为 true；
    /// Team 工作流审批桥为 false（成员策略固定 PermissionMode.Team，桥只做单次批准）。
    /// </summary>
    bool AllowSessionEscalation = true);

public sealed record PermissionPromptResult(PermissionPromptDecision Decision);

public enum PermissionPromptDecision
{
    Allow,
    AllowAlways,
    /// <summary>本次对话全部允许——当前调用照常批准，其余工具调用本 run 与后续 run 均不再询问。</summary>
    AllowAllConversation,
    Deny,
}
