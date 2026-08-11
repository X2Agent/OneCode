using OneCode.App.Services.Skills;
using OneCode.Infrastructure.Skills;

namespace OneCode.App.Commands;

/// <summary>Dynamic slash commands backed by the unified skill catalog.</summary>
public sealed class SkillCommandSource(SkillCatalog catalog) : IDynamicCommandSource
{
    public CommandSource Source => CommandSource.Skill;

    public Task<IReadOnlyList<ICommand>> LoadCommandsAsync(CancellationToken ct)
    {
        IReadOnlyList<ICommand> commands = catalog.LoadUserInvocableSkills()
            .Select(skill => (ICommand)new SkillProxyCommand(catalog, skill))
            .ToList();
        return Task.FromResult(commands);
    }
}

internal sealed class SkillProxyCommand(SkillCatalog catalog, SkillDocument skill) : Command
{
    public override string Name => skill.Name;
    public override string Description => skill.Description;
    public override string? ArgumentHint => skill.ArgumentHint;
    public override CommandCategory Category => CommandCategory.Skill;
    public override CommandSource Source => CommandSource.Skill;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        // Resolve again at invocation time so frontmatter/body edits take effect even before
        // the dynamic command surface has completed its debounce refresh.
        var current = catalog.Find(skill.Name);
        if (current is null)
            return Task.FromResult(CommandResult.Error($"Skill '{skill.Name}' not found."));
        return Task.FromResult(CommandResult.Prompt(SkillCatalog.Render(current, args)));
    }
}
