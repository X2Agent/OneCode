namespace OneCode.App.Commands;

using OneCode.Core.Text;

/// <summary>
/// DI-driven command registry. Built-in commands are discovered via <c>IEnumerable&lt;ICommand&gt;</c>
/// injected by the container. Dynamic commands (plugins/MCP/skills) are added at runtime.
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly List<ICommand> _commands;
    private readonly Lock _lock = new();

    public CommandRegistry(IEnumerable<ICommand> builtinCommands)
    {
        _commands = [.. builtinCommands];
    }

    public IReadOnlyList<ICommand> GetAll(bool includeDisabled = false)
    {
        lock (_lock)
        {
            return _commands
                .Where(c => includeDisabled || IsEnabled(c))
                .ToList()
                .AsReadOnly();
        }
    }

    public IReadOnlyList<ICommand> GetByCategory(CommandCategory category)
    {
        lock (_lock)
        {
            return _commands
                .Where(c => GetCategory(c) == category && IsEnabled(c))
                .ToList()
                .AsReadOnly();
        }
    }

    public IReadOnlyDictionary<CommandCategory, IReadOnlyList<ICommand>> GetGrouped()
    {
        lock (_lock)
        {
            return _commands
                .Where(c => IsEnabled(c))
                .GroupBy(c => GetCategory(c))
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<ICommand>)g.ToList().AsReadOnly());
        }
    }

    public ICommand? Find(string input)
    {
        var name = ExtractCommandName(input);
        lock (_lock)
        {
            return _commands.FirstOrDefault(c =>
                c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                GetAliases(c).Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public bool IsCommand(string input) => Find(input) is not null;

    public string? Suggest(string input)
    {
        var name = ExtractCommandName(input);
        var bestScore = 0.0;
        string? bestName = null;

        lock (_lock)
        {
            foreach (var command in _commands)
            {
                if (!IsEnabled(command) || IsHidden(command)) continue;

                foreach (var candidate in GetAliases(command).Prepend(command.Name))
                {
                    var score = StringDistance.JaroWinkler(candidate, name);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestName = candidate;
                    }
                }
            }
        }

        return bestScore > 0.7 ? $"Did you mean '/{bestName}'?" : null;
    }

    private static string ExtractCommandName(string input) =>
        input.TrimStart('/').Split(' ')[0];

    public void Register(ICommand command)
    {
        lock (_lock) { _commands.Add(command); }
    }

    public void RegisterRange(IEnumerable<ICommand> commands)
    {
        lock (_lock) { _commands.AddRange(commands); }
    }

    public void UnregisterBySource(CommandSource source)
    {
        lock (_lock) { _commands.RemoveAll(c => GetSource(c) == source); }
    }

    public async Task RefreshDynamicCommandsAsync(IEnumerable<IDynamicCommandSource> sources, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            UnregisterBySource(source.Source);
            var commands = await source.LoadCommandsAsync(ct).ConfigureAwait(false);
            if (commands.Count > 0)
                RegisterRange(commands);
        }
    }

    // Metadata helpers — ICommand 现以默认接口成员直接暴露元数据（原 ICommandMetadata 已并入）。

    private static bool IsEnabled(ICommand c) => c.IsEnabled();

    private static bool IsHidden(ICommand c) => c.IsHidden;

    private static CommandCategory GetCategory(ICommand c) => c.Category;

    private static IReadOnlyList<string> GetAliases(ICommand c) => c.Aliases;

    private static CommandSource GetSource(ICommand c) => c.Source;
}
