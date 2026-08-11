namespace OneCode.App.Services;

public sealed record TuiCommandSurfaceDependencies(
    ICommandRegistry CommandRegistry,
    IEnumerable<IDynamicCommandSource> DynamicCommandSources,
    IStartupHintCollector StartupHintCollector);
