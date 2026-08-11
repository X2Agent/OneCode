using OneCode.App.Services;

namespace OneCode.App.Commands;

/// <summary>
/// /queue — 管理输入队列（对话完成后自动取下一条继续执行）。
///
/// 用法：
///   /queue add &lt;text&gt;     添加输入到队列末尾
///   /queue list              列出队列中的所有输入（带索引）
///   /queue drop &lt;index&gt;    移除指定索引的输入
///   /queue clear             清空队列
///   /queue                   等同于 list
///
/// 队列是内存中的单队列——query 运行时用户输入会自动入队，query 完成后自动出队执行。
/// 也可通过此命令主动预排任务序列。
/// </summary>
public sealed class QueueCommand(InputQueue inputQueue) : Command
{
    public override string Name => "queue";
    public override string Description => "Manage the input queue (auto-executes after each conversation)";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ArgumentHint => "add <text> | list | drop <index> | clear";

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return Task.FromResult(List());

        var sub = args[0].ToLowerInvariant();
        var result = sub switch
        {
            "add" or "a" => Add(args),
            "list" or "ls" => List(),
            "drop" or "remove" or "rm" => Drop(args),
            "clear" => Clear(),
            _ => CommandResult.Error($"Unknown subcommand '{args[0]}'. Usage: /queue {ArgumentHint}")
        };
        return Task.FromResult(result);
    }

    private CommandResult Add(string[] args)
    {
        if (args.Length < 2)
            return CommandResult.Error("Usage: /queue add <text>");

        var prompt = string.Join(' ', args[1..]);
        if (string.IsNullOrWhiteSpace(prompt))
            return CommandResult.Error("Input text cannot be empty.");

        inputQueue.Enqueue(prompt);
        return CommandResult.Text($"Queued ({inputQueue.Count} total): {Truncate(prompt, 80)}");
    }

    private CommandResult List()
    {
        var items = inputQueue.PeekAll();
        if (items.Count == 0)
            return CommandResult.Text("Queue is empty.");

        var lines = new List<string> { $"Input queue ({items.Count}):" };
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var preview = Truncate(item.Text.Replace('\n', ' '), 70);
            var imgCount = item.Images?.Count ?? 0;
            var suffix = imgCount > 0 ? $" [+{imgCount} img]" : "";
            lines.Add($"  [{i}] {preview}{suffix}");
        }
        lines.Add("Next input auto-executes when current conversation completes.");
        return CommandResult.Text(string.Join('\n', lines));
    }

    private CommandResult Drop(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var index))
            return CommandResult.Error("Usage: /queue drop <index>  (use /queue list to see indices)");

        var removed = inputQueue.RemoveAt(index);
        return removed
            ? CommandResult.Text($"Removed input at index {index}.")
            : CommandResult.Error($"Index {index} out of range. Use /queue list to see valid indices.");
    }

    private CommandResult Clear()
    {
        inputQueue.Clear();
        return CommandResult.Text("Queue cleared.");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
