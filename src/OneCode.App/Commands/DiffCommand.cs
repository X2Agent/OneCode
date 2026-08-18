namespace OneCode.App.Commands;

/// <summary>
/// /diff — Git 变更审查。
/// TUI 路径：裸 /diff 由 <c>OneCodeToplevel</c> 拦截并弹出 ReviewOverlay 图形化文件列表；
/// 本命令负责带 --staged 或 file-path 参数时的原始 git diff 文本输出，
/// 并作为非 TUI 宿主的兜底。
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
