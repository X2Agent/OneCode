namespace OneCode.Core.Permissions.Yolo;

public sealed record UserRule(
    string Type,
    string Pattern,
    string Description)
{
    public bool IsAllow => Type.Equals("allow", StringComparison.OrdinalIgnoreCase);
    public bool IsSoftDeny => Type.Equals("soft_deny", StringComparison.OrdinalIgnoreCase);
    public bool IsDeny => Type.Equals("deny", StringComparison.OrdinalIgnoreCase);
}
