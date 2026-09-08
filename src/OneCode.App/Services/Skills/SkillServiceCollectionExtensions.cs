using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.Skills;

/// <summary>
/// Skills 领域 DI 注册——与技能实现（<see cref="SkillCatalog"/> / <see cref="SkillChangeWatcher"/> /
/// <see cref="McpSkillsIntegrator"/>）同目录维护，注册代码不得漂移到按启动批次分组的 partial 桶。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class SkillServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MAF AgentSkillsProvider 作为单例，涵盖 managed/user/project 三个技能目录
    /// 以及 BundledSkills 内置技能。MCP 技能在构建时通过 <see cref="McpSkillsIntegrator"/> 注入。
    /// </summary>
    public static IServiceCollection AddSkillServices(
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
