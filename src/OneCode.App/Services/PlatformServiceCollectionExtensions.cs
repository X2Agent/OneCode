using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.IO;
using OneCode.Core.Lsp;
using OneCode.App.Services.Notifier;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Media;
using OneCode.Infrastructure.Remote;

namespace OneCode.App.Services;

/// <summary>
/// 平台适配服务 DI 注册——跨域消费的基础设施适配（工作流运行时存储、远程执行、
/// 代码索引、VCR 录制回放、通知、图像管线）。实现均在 Infrastructure，
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        // 核心原语（Core 接口 → Infrastructure 实现，无领域归属的底层组合）。
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<LocalAgentFileStore>();
        services.AddSingleton<IFileSystem>(sp => sp.GetRequiredService<LocalAgentFileStore>());
        services.AddSingleton<OneCode.Core.IO.IClipboardService, ClipboardService>();
        services.AddSingleton<OneCode.Core.ITokenEstimator, OneCode.Infrastructure.TokenEstimator>();

        services.AddSingleton<Core.Workflows.IWorkflowRunRegistry, OneCode.Infrastructure.Workflows.JsonWorkflowRunRegistry>();
        services.AddSingleton<Core.Workflows.IOperationLedger>(
            new OneCode.Infrastructure.Workflows.FileOperationLedger());

        services.AddSingleton<SshRemoteService>();

        services.AddSingleton<ICodeIndexService, CodeIndexService>();
        services.AddSingleton<CodeIndexHotReloader>();

        // VCR (录像/回放) — Infrastructure 层基础设施，注册下沉到 AddVcrServices()。
        // 未设 ONECODE_VCR 环境变量时零开销透传，不影响生产路径。
        services.AddVcrServices();

        // WebSearch 提供方（Tavily）— App 层 WebSearchTool 按设置组装故障转移链。
        services.AddWebSearchProviders();

        services.AddSingleton<NotifierService>();
        services.AddSingleton<INotifierService>(sp => sp.GetRequiredService<NotifierService>());

        services.AddSingleton<ImagePipeline>();

        return services;
    }
}
