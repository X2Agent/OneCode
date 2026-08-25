namespace OneCode.Core.Commands;

/// <summary>
/// 斜杠命令参数的轻量解析视图。
/// 约定（与 docs/commands.md 一致）：
/// - 非 <c>-</c> 前缀的 token 为位置参数，首个位置参数即子命令；
/// - 值型旗标消费下一个非旗标 token 作为值；
/// - 其余 <c>-</c> 旗标为布尔标志；<c>--key=value</c> 内联赋值同样支持。
/// </summary>
public sealed class CommandArgs
{
    private readonly HashSet<string> _flags;
    private readonly Dictionary<string, string> _values;

    private CommandArgs(
        IReadOnlyList<string> positionals,
        HashSet<string> flags,
        Dictionary<string, string> values)
    {
        Positionals = positionals;
        _flags = flags;
        _values = values;
    }

    /// <summary>全部位置参数。</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>是否存在位置参数。</summary>
    public bool HasPositionals => Positionals.Count > 0;

    /// <summary>首个位置参数（小写），无则 null——即子命令。</summary>
    public string? SubCommand =>
        Positionals.Count > 0 ? Positionals[0].ToLowerInvariant() : null;

    /// <summary>除首个（子命令）之外的其余位置参数。</summary>
    public IReadOnlyList<string> Rest =>
        Positionals.Count > 1 ? Positionals.Skip(1).ToArray() : [];

    /// <summary>
    /// 解析参数。<paramref name="valueFlags"/> 中列出的旗标名消费下一个非旗标 token 作为值，
    /// 其余 <c>-</c> 前缀旗标视为布尔标志。
    /// </summary>
    public static CommandArgs Parse(IReadOnlyList<string> args, IEnumerable<string>? valueFlags = null)
    {
        var valueSet = valueFlags?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                       ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<string> positionals = [];
        HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-'))
            {
                positionals.Add(token);
                continue;
            }

            var name = token.TrimStart('-');

            // 内联赋值：--key=value
            var eq = name.IndexOf('=');
            if (eq > 0)
            {
                values[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            // 值型旗标：仅当存在后续 token 且其不是旗标时消费
            if (valueSet.Contains(name) && i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                values[name] = args[++i];
            else
                flags.Add(name);
        }

        return new CommandArgs(positionals, flags, values);
    }

    /// <summary>布尔标志是否出现。</summary>
    public bool Has(string name) => _flags.Contains(name);

    /// <summary>旗标值；未提供时返回 null。</summary>
    public string? Value(string name) => _values.GetValueOrDefault(name);

    /// <summary>剩余位置参数拼接为单个字符串（空格连接）；无则 null。</summary>
    public string? JoinedRest => Rest.Count > 0 ? string.Join(' ', Rest) : null;
}
