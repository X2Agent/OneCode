using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Commands;

/// <summary>
/// MCP 动态命令来源（C-04）。
/// 从已连接的 MCP 服务器生成 /mcp:{server} 命令，
/// 用户可通过该命令与指定 MCP 服务器进行交互。
/// </summary>
public sealed class McpCommandSource : IDynamicCommandSource
{
    private readonly IMcpConnectionManager _connectionManager;

    public McpCommandSource(IMcpConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public CommandSource Source => CommandSource.Mcp;

    public Task<IReadOnlyList<ICommand>> LoadCommandsAsync(CancellationToken ct)
    {
        var commands = new List<ICommand>();

        foreach (var serverName in _connectionManager.GetServerNames())
        {
            commands.Add(new McpServerCommand(serverName));
        }

        return Task.FromResult<IReadOnlyList<ICommand>>(commands);
    }
}

/// <summary>
/// MCP 服务器交互命令：显示服务器状态与可用工具信息。
/// </summary>
internal sealed class McpServerCommand : Command
{
    private readonly string _serverName;

    public McpServerCommand(string serverName)
    {
        _serverName = serverName;
    }

    public override string Name => $"mcp:{_serverName}";
    public override string Description => $"Interact with MCP server '{_serverName}'";
    public override CommandCategory Category => CommandCategory.Skill;
    public override CommandSource Source => CommandSource.Mcp;
    public override bool IsHidden => true;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        // MCP servers are primarily tool-based; this command provides
        // a quick status check and delegates to the tool system for actual interaction.
        return Task.FromResult(CommandResult.Text(
            $"MCP server: {_serverName}\n" +
            $"Use the MCP tools registered by this server directly in your conversation.\n" +
            $"Run /mcp for full MCP server management."));
    }
}
