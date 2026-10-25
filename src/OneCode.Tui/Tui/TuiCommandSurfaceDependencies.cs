using OneCode.App.Services;

namespace OneCode.Tui;

public sealed record TuiCommandSurfaceDependencies(
    ICommandRegistry CommandRegistry,
    IEnumerable<IDynamicCommandSource> DynamicCommandSources,
    IStartupHintCollector StartupHintCollector);
