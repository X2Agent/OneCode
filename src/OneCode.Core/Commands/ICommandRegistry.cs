namespace OneCode.Core.Commands;

/// <summary>
/// Registry for discovering and resolving slash commands.
/// Supports both built-in commands (discovered via DI) and dynamic commands (plugins/MCP/skills).
/// </summary>
public interface ICommandRegistry
{
    IReadOnlyList<ICommand> GetAll(bool includeDisabled = false);

    IReadOnlyList<ICommand> GetByCategory(CommandCategory category);

    IReadOnlyDictionary<CommandCategory, IReadOnlyList<ICommand>> GetGrouped();

    ICommand? Find(string input);

    bool IsCommand(string input);

    /// <summary>
    /// Returns a "Did you mean '/{name}'?" hint when the provided command name
    /// closely resembles a known command, or <c>null</c> when no good match exists.
    /// </summary>
    string? Suggest(string input);

    void Register(ICommand command);

    void RegisterRange(IEnumerable<ICommand> commands);

    void UnregisterBySource(CommandSource source);

    /// <summary>
    /// 刷新所有动态命令来源（C-04）：先清除旧命令，再加载新命令。
    /// </summary>
    Task RefreshDynamicCommandsAsync(IEnumerable<IDynamicCommandSource> sources, CancellationToken ct);
}
