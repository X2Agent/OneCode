using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Commands;

public static class CommandServiceExtensions
{
    /// <summary>
    /// Registers all <see cref="ICommand"/> implementations and the <see cref="ICommandRegistry"/>.
    /// Uses explicit type registration instead of reflection for AOT/trimming compatibility.
    /// When adding a new command, register it in <see cref="RegisterAllCommands"/>.
    /// </summary>
    public static IServiceCollection AddCommands(this IServiceCollection services)
    {
        // Shared utility services used by commands (registered via interfaces per DIP)
        services.AddSingleton<IGitHelper, GitHelper>();
        // IClipboardService is registered in RegisterCoreServices (Infrastructure implementation)

        RegisterAllCommands(services);

        services.AddSingleton<ICommandRegistry, CommandRegistry>();
        services.AddSingleton<IAppStateAccessor, AppStateAccessor>();

        return services;
    }

    private static void RegisterAllCommands(IServiceCollection services)
    {
        AddBuiltinCommands(services);
        AddSessionCommands(services);
        AddDiagnosticCommands(services);
        AddGitCommands(services);
        AddMiscCommands(services);
    }

    private static void AddBuiltinCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, AddDirCommand>();
        s.AddSingleton<ICommand, CompactCommand>();
        s.AddSingleton<ICommand, CopyCommand>();
        s.AddSingleton<ICommand, CronCommand>();
        s.AddSingleton<ICommand, DesignInitCommand>();
        s.AddSingleton<ICommand, ExportCommand>();
        s.AddSingleton<ICommand, FastModelCommand>();
        s.AddSingleton<ICommand, FilesCommand>();
        s.AddSingleton<ICommand, HelpCommand>();
        s.AddSingleton<ICommand, HooksCommand>();
        s.AddSingleton<ICommand, InitCommand>();
        s.AddSingleton<ICommand, KeybindingsCommand>();
        s.AddSingleton<ICommand, LspCommand>();
        s.AddSingleton<ICommand, ModelCommand>();
        s.AddSingleton<ICommand, PermissionsCommand>();
        s.AddSingleton<ICommand, QueueCommand>();
        s.AddSingleton<ICommand, ReviewCommand>();
        s.AddSingleton<ICommand, ThinkCommand>();
        s.AddSingleton<ICommand, ToolsCommand>();
        s.AddSingleton<ICommand, UpgradeCommand>();
        s.AddSingleton<ICommand, VersionCommand>();
        s.AddSingleton<ICommand, PromptsCommand>();
    }

    private static void AddSessionCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, InsightsCommand>();
        s.AddSingleton<ICommand, MemoryCommand>();
        s.AddSingleton<ICommand, RenameCommand>();
        s.AddSingleton<ICommand, CheckpointCommand>();
        s.AddSingleton<ICommand, SessionCommand>();
        s.AddSingleton<ICommand, FindCommand>();
    }

    private static void AddDiagnosticCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, DoctorCommand>();
        s.AddSingleton<ICommand, StatusCommand>();
        s.AddSingleton<ICommand, GcStatsCommand>();
    }

    private static void AddGitCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, BranchCommand>();
        s.AddSingleton<ICommand, CommitCommand>();
        s.AddSingleton<ICommand, DiffCommand>();
        s.AddSingleton<ICommand, RebaseCommand>();
        s.AddSingleton<ICommand, StashCommand>();
    }

    private static void AddMiscCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, ConfigCommand>();
        s.AddSingleton<ICommand, ExitCommand>();
        s.AddSingleton<ICommand, InstallCommand>();
        s.AddSingleton<ICommand, McpCommand>();
        s.AddSingleton<ICommand, SkillsCommand>();
        s.AddSingleton<ICommand, TeamCommand>();
    }
}
