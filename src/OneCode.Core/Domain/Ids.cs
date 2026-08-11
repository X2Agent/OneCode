using System.Text.RegularExpressions;

namespace OneCode.Core.Domain;

/// <summary>
/// 会话 ID——编译时类型安全的品牌类型。
/// Value 为 32 位无连字符十六进制（Guid.ToString("N")）。
/// </summary>
public readonly partial record struct SessionId(string Value) : IEquatable<SessionId>
{
    public override string ToString() => Value;

    public static implicit operator string(SessionId id) => id.Value;
    public static implicit operator SessionId(string s) => new(s);

    public static SessionId NewId() => new(Guid.NewGuid().ToString("N"));

    public static SessionId? TryParse(string? s)
        => !string.IsNullOrEmpty(s) && SafePattern().IsMatch(s) ? new SessionId(s) : (SessionId?)null;

    [GeneratedRegex(@"^[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SafePattern();
}

/// <summary>
/// 代理 ID——编译时类型安全的品牌类型。
/// AgentId 格式：a + 可选 &lt;label&gt;- + 16位十六进制
/// 例如：a1a2b3c4d5e6f7a8, amyagent-1a2b3c4d5e6f7a8
/// </summary>
public readonly partial record struct AgentId(string Value)
{
    [GeneratedRegex(@"^a(?:.+-)?[0-9a-f]{16}$")]
    private static partial Regex Pattern();

    public override string ToString() => Value;

    public static implicit operator string(AgentId id) => id.Value;

    public static explicit operator AgentId(string s) => new(s);

    public static AgentId? TryParse(string s)
        => Pattern().IsMatch(s) ? new AgentId(s) : null;

    public static AgentId NewId()
    {
        Span<byte> bytes = stackalloc byte[8];
        Random.Shared.NextBytes(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return new AgentId($"a{hex}");
    }
}
