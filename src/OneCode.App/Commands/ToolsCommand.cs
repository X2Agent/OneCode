using IAppStateAccessor = OneCode.Core.Domain.IAppStateAccessor;
using System.Text;

namespace OneCode.App.Commands;

/// <summary>
/// /tools — list available tools registered in the current app state.
/// </summary>
public sealed class ToolsCommand : Command
{
    private readonly IAppStateAccessor _appState;
    private readonly ToolMetadataRegistry _metadata;

    public ToolsCommand(IAppStateAccessor appState, ToolMetadataRegistry metadata)
    {
        _appState = appState;
        _metadata = metadata;
    }

    public override string Name => "tools";
    public override string Description => "List available tools";
    public override CommandCategory Category => CommandCategory.Builtin;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var tools = _appState.Current.Tools;
        if (tools.Count == 0)
            return Task.FromResult(CommandResult.Text("No tools registered."));

        var sb = new StringBuilder($"Available tools ({tools.Count}):");
        foreach (var tool in tools.OrderBy(t => t.Name))
        {
            var hint = _metadata.Get(tool.Name)?.SearchHint ?? "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {tool.Name,-24} {hint}");
        }

        return Task.FromResult(CommandResult.Text(sb.ToString().TrimEnd()));
    }
}
