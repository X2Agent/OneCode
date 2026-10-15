namespace OneCode.App.Session;

/// <summary>
/// 会话 id 解析结果：唯一命中 / 歧义候选 / 零命中错误。三者互斥，
/// 调用方按 <see cref="Id"/>、<see cref="Candidates"/>、<see cref="Error"/> 的非空性分派。
/// </summary>
public sealed record SessionResolution(
    SessionId? Id,
    string? Error,
    IReadOnlyList<ConversationSummary> Candidates)
{
    public static SessionResolution Resolved(SessionId id) => new(id, null, []);

    public static SessionResolution NotFound(string input) =>
        new(null, $"Session '{input}' not found.", []);

    public static SessionResolution Ambiguous(IReadOnlyList<ConversationSummary> candidates) =>
        new(null, null, candidates);
}

/// <summary>
/// 会话 id 前缀解析：完整 32 位 id 直通（<see cref="SessionId.TryParse"/>），
/// 否则在候选列表内按 OrdinalIgnoreCase 前缀匹配——唯一命中返回完整 id，
/// 多命中返回全部候选（由调用方列出短 id + 标题），零命中返回 not found。
/// 纯函数，候选列表由调用方先 <see cref="ISessionManager.ListAsync"/> 取得。
/// </summary>
public static class SessionIdResolver
{
    public static SessionResolution Resolve(string input, IReadOnlyList<ConversationSummary> candidates)
    {
        if (SessionId.TryParse(input) is { } exact)
            return SessionResolution.Resolved(exact);

        if (string.IsNullOrWhiteSpace(input))
            return SessionResolution.NotFound(input);

        var matches = candidates
            .Where(c => c.Id.Value.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => SessionResolution.Resolved(matches[0].Id),
            > 1 => SessionResolution.Ambiguous(matches),
            _ => SessionResolution.NotFound(input),
        };
    }

    /// <summary>8 位短 id（list 输出与歧义候选展示用；前缀匹配保证其唯一性语义）。</summary>
    public static string ShortId(SessionId id) => ShortId(id.Value);

    public static string ShortId(string idValue) => idValue.Length > 8 ? idValue[..8] : idValue;

    /// <summary>歧义候选展示行：短 id + 标题 + 模式 + 消息数。</summary>
    public static string DescribeCandidate(ConversationSummary s)
        => $"{ShortId(s.Id)}  {s.Title ?? "(未命名)"}  ({s.Mode ?? "build"}, {s.MessageCount} msgs)";

    /// <summary>格式化前缀匹配命中多条候选时的错误提示（最多列出前 10 条）。</summary>
    public static string FormatAmbiguousError(IReadOnlyList<ConversationSummary> candidates)
    {
        var sb = new System.Text.StringBuilder($"Ambiguous session id — {candidates.Count} matches. Use a longer prefix:");
        foreach (var c in candidates.Take(10))
            sb.Append('\n').Append("  ").Append(DescribeCandidate(c));
        return sb.ToString();
    }
}
