namespace OneCode.App.Commands;

/// <summary>
/// /diff — Git 变更审查。
/// 无参数时在 TUI 中弹出 ReviewOverlay 图形化文件列表；
/// 带 --staged 或 file-path 参数时输出原始 git diff 文本。
/// </summary>
public sealed class DiffCommand(IGitHelper gitHelper) : Command
{
    public override string Name => "diff";
    public override string Description => "Review git changes (overlay in TUI, text with args)";
    public override CommandCategory Category => CommandCategory.Git;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[--staged] [file-path]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var diffArgs = new List<string> { "diff" };
        if (args.Contains("--staged")) diffArgs.Add("--staged");

        var filePaths = args.Where(a => a != "--staged" && !a.StartsWith('-')).ToArray();
        if (filePaths.Length > 0)
        {
            diffArgs.Add("--");
            diffArgs.AddRange(filePaths);
        }

        var result = await gitHelper.RunAsync([.. diffArgs], ct).ConfigureAwait(false);
        if (result is null)
            return CommandResult.Error("git is not available.");

        var output = result.Success ? result.Stdout : result.Stderr;
        return string.IsNullOrWhiteSpace(output)
            ? CommandResult.Text("No changes to show.")
            : CommandResult.Text(output.TrimEnd());
    }
}
