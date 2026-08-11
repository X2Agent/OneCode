using System.Reflection;
using OneCode.Core.Product;
namespace OneCode.App.Commands;

public sealed class VersionCommand : Command
{
    public override string Name => "version";
    public override string Description => "Show version information";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override IReadOnlyList<string> Aliases => ["v"];

    public static string Version { get; } =
        typeof(VersionCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(VersionCommand).Assembly.GetName().Version?.ToString(3)
        ?? ProductInfo.Default.Version;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default) =>
        Task.FromResult(CommandResult.Text($"OneCode CLI v{Version} (.NET implementation)"));
}
