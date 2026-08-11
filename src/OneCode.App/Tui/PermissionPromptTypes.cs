namespace OneCode.App.Tui;

/// <param name="AllowApprovals">
/// When false, the prompt only offers Deny (parse failure / fail-closed).
/// </param>
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
