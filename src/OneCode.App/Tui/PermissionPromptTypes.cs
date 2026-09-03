namespace OneCode.App.Tui;

public sealed record PermissionPromptRequest(
    string Title,
    string Message,
    bool AllowApprovals = true);

public sealed record PermissionPromptResult(PermissionPromptDecision Decision);

public enum PermissionPromptDecision
{
    Allow,
    AllowAlways,
    Deny,
}
