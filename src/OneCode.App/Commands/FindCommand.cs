namespace OneCode.App.Commands;

/// <summary>
/// /find — 会话内搜索。TUI 路径由 <c>OneCodeToplevel</c> 拦截并滚动到匹配行；
/// 本命令负责出现在斜杠补全中，并在无参数时给出用法提示。
/// </summary>
public sealed class FindCommand : Command
{
    public override string Name => "find";
    public override string Description => "Search conversation transcript and scroll to match";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "<keyword>";
    public override IReadOnlyList<string> Aliases => ["search"];

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return Task.FromResult(CommandResult.Text("用法: /find <关键词> — 搜索会话内容并滚动到匹配位置"));

        // TUI Dispatch intercepts /find before ExecuteCommand; this path is a
        // fallback for non-TUI hosts that still surface the command.
        var query = string.Join(' ', args);
        return Task.FromResult(CommandResult.Text(
            $"搜索 \"{query}\" — 请在交互式 TUI 中使用 /find 以跳转到匹配位置"));
    }
}
