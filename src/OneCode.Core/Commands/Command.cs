namespace OneCode.Core.Commands;

/// <summary>
/// Abstract base class for slash commands.
/// Provides default implementations for all optional metadata members via
/// <c>virtual</c> properties so concrete commands only override what they need.
/// New commands should inherit from this class rather than implementing <see cref="ICommand"/> directly.
/// </summary>
public abstract class Command : ICommand
{
    // ICommand 核心 3 成员（必须重写）

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default);

    // 元数据默认实现（可选重写）

    public virtual CommandCategory Category => CommandCategory.Builtin;
    public virtual IReadOnlyList<string> Aliases => Array.Empty<string>();
    public virtual bool IsHidden => false;
    public virtual bool IsEnabled() => true;
    public virtual string? ArgumentHint => null;
    public virtual string? ProgressMessage => null;
    public virtual bool Immediate => false;
    public virtual CommandSource Source => CommandSource.Builtin;

    /// <summary>
    /// 加载并渲染 prompt 模板。返回 null 表示模板不存在。
    /// （统一 Commit/Review/Init/DesignInit 四处逐字相同的私有实现。）
    /// </summary>
    protected static async Task<string?> LoadPromptAsync(
        OneCode.Core.Prompt.IPromptManager promptManager,
        string name,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct)
    {
        var loaded = await promptManager.GetPromptAsync(name, ct).ConfigureAwait(false);
        if (loaded is null)
            return null;
        return new OneCode.Core.Prompt.PromptTemplate(name, loaded).Render(variables);
    }

    /// <summary>
    /// 解析 "--flag value" 形式的参数值；未提供时返回 null。
    /// （统一 Review/DesignInit 两处字符级相同的私有实现。）
    /// </summary>
    protected static string? ParseFlag(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
