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
        // HelpCommand 注入 Func<ICommandRegistry> 以打破 CommandRegistry → IEnumerable<ICommand> → HelpCommand 循环
        services.AddSingleton<Func<ICommandRegistry>>(sp => () => (ICommandRegistry)sp.GetRequiredService(typeof(ICommandRegistry)));
        services.AddSingleton<IAppStateAccessor, AppStateAccessor>();

        return services;
    }

    private static void RegisterAllCommands(IServiceCollection services)
    {
        AddBuiltinCommands(services);
        AddSessionCommands(services);
        AddDiagnosticCommands(services);
        AddSkillCommands(services);
        AddGitCommands(services);
    }

    private static void AddBuiltinCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, AddDirCommand>();
        s.AddSingleton<ICommand, CompactCommand>();
        s.AddSingleton<ICommand, ConfigCommand>();
        s.AddSingleton<ICommand, CopyCommand>();
        s.AddSingleton<ICommand, CronCommand>();
        s.AddSingleton<ICommand, DesignInitCommand>();
        s.AddSingleton<ICommand, ExitCommand>();
        s.AddSingleton<ICommand, FastModelCommand>();
        s.AddSingleton<ICommand, FilesCommand>();
        s.AddSingleton<ICommand, HelpCommand>();
        s.AddSingleton<ICommand, HooksCommand>();
        s.AddSingleton<ICommand, InitCommand>();
        s.AddSingleton<ICommand, KeybindingsCommand>();
        s.AddSingleton<ICommand, LspCommand>();
        s.AddSingleton<ICommand, ModelCommand>();
        s.AddSingleton<ICommand, PermissionsCommand>();
        s.AddSingleton<ICommand, PromptsCommand>();
        s.AddSingleton<ICommand, SkillsCommand>();
        s.AddSingleton<ICommand, TeamCommand>();
        s.AddSingleton<ICommand, ThinkCommand>();
        s.AddSingleton<ICommand, UpgradeCommand>();
        s.AddSingleton<ICommand, VersionCommand>();
    }

    private static void AddSessionCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, CheckpointCommand>();
        s.AddSingleton<ICommand, ExportCommand>();
        s.AddSingleton<ICommand, FindCommand>();
        s.AddSingleton<ICommand, InsightsCommand>();
        s.AddSingleton<ICommand, MemoryCommand>();
        s.AddSingleton<ICommand, QueueCommand>();
        s.AddSingleton<ICommand, RenameCommand>();
        s.AddSingleton<ICommand, ResumeCommand>();

        // SessionCommand 注入具体类型以委托 new/close 子命令，
        // 因此具体注册 + ICommand 转发保证两个视角共享同一单例。
        s.AddSingleton<NewCommand>();
        s.AddSingleton<ICommand>(sp => sp.GetRequiredService<NewCommand>());
        s.AddSingleton<CloseCommand>();
        s.AddSingleton<ICommand>(sp => sp.GetRequiredService<CloseCommand>());
        s.AddSingleton<ICommand, SessionCommand>();
    }

    private static void AddDiagnosticCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, DoctorCommand>();
        s.AddSingleton<ICommand, StatusCommand>();
        s.AddSingleton<ICommand, GcStatsCommand>();
    }

    private static void AddSkillCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, InstallCommand>();
        s.AddSingleton<ICommand, McpCommand>();
    }

    private static void AddGitCommands(IServiceCollection s)
    {
        s.AddSingleton<ICommand, BranchCommand>();
        s.AddSingleton<ICommand, CommitCommand>();
        s.AddSingleton<ICommand, DiffCommand>();
        s.AddSingleton<ICommand, RebaseCommand>();
        s.AddSingleton<ICommand, ReviewCommand>();
        s.AddSingleton<ICommand, StashCommand>();
    }
}
