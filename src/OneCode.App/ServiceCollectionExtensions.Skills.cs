using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.Skills;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MAF AgentSkillsProvider 作为单例，涵盖 managed/user/project 三个技能目录
    /// 以及 BundledSkills 内置技能。MCP 技能在构建时通过 <see cref="McpSkillsIntegrator"/> 注入。
    /// </summary>
    public static IServiceCollection RegisterSkillServices(
        this IServiceCollection services, string workingDir)
    {
        services.AddSingleton(new SkillCatalog(workingDir));

        // Async factory shared by DI singleton and SkillChangeWatcher hot-reload.
        // Includes MCP skills so hot-reload preserves them.
        services.AddSingleton<Func<Task<AgentSkillsProvider>>>(sp =>
        {
            var integrator = sp.GetRequiredService<McpSkillsIntegrator>();
            var scriptLogger = sp.GetService<ILogger<SkillChangeWatcher>>();
            return async () =>
            {
                var builder = new AgentSkillsProviderBuilder();
                AgentSkillsProviderFactory.ConfigureFileAndBundledSkills(
                    builder, sp.GetRequiredService<SkillCatalog>(), scriptLogger);
                await integrator.ApplyAsync(builder, CancellationToken.None).ConfigureAwait(false);
                return builder.Build();
            };
        });

        services.AddSingleton<AgentSkillsProvider>(sp =>
            sp.GetRequiredService<Func<Task<AgentSkillsProvider>>>()().GetAwaiter().GetResult());
        services.AddSingleton<SkillProviderHolder>();

        services.AddSingleton<McpSkillsIntegrator>();

        services.AddSingleton<SkillChangeWatcher>(sp =>
            new SkillChangeWatcher(
                sp.GetRequiredService<ILogger<SkillChangeWatcher>>(),
                sp.GetRequiredService<SkillProviderHolder>(),
                sp.GetRequiredService<Func<Task<AgentSkillsProvider>>>(),
                sp.GetRequiredService<SkillCatalog>()));
        services.AddHostedService(sp => sp.GetRequiredService<SkillChangeWatcher>());

        return services;
    }
}
