using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.Skills;

/// <summary>
/// Skills 领域 DI 注册——与技能实现（<see cref="SkillCatalog"/> / <see cref="SkillProviderFactory"/> /
/// <see cref="SkillFilesWatcher"/>）同目录维护，注册代码不得漂移到按启动批次分组的 partial 桶。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class SkillServiceCollectionExtensions
{
    /// <summary>
    /// 注册技能源工厂（managed/user/project 三个技能目录 + BundledSkills 内置技能 +
    /// 已连接 MCP 服务器的 skill:// 技能源）与技能文件变更通知服务。
    /// </summary>
    /// <remarks>
    /// 只注册 <b>工厂</b>，不注册 provider：<see cref="AgentSkillsProvider"/> 在每次 agent run 时
    /// 由 <see cref="SkillProviderFactory"/> 构造，使 MCP 服务器在运行期连接/断开后被自动反映，
    /// 无需原子替换 provider。斜杠命令发现直接读文件（<see cref="SkillCatalog"/>），
    /// 由 <see cref="SkillFilesWatcher.SkillsChanged"/> 通知 UI 重渲染。
    /// </remarks>
    public static IServiceCollection AddSkillServices(
        this IServiceCollection services, string workingDir)
    {
        services.AddSingleton(new SkillCatalog(workingDir));

        services.AddSingleton<SkillProviderFactory>();

        services.AddSingleton<SkillFilesWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<SkillFilesWatcher>());

        return services;
    }
}
