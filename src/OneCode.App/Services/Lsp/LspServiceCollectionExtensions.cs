using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Tools;
using OneCode.Core.Lsp;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// LSP 领域 DI 注册——与语言服务器实现（<see cref="LspServerManager"/> /
/// <see cref="EnhancedLspService"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class LspServiceCollectionExtensions
{
    public static IServiceCollection AddLspServices(this IServiceCollection services)
    {
        services.AddSingleton<LspDiagnosticRegistry>();
        services.AddSingleton<EnhancedLspService>();
        services.AddSingleton<IEnhancedLspService>(sp => sp.GetRequiredService<EnhancedLspService>());
        services.AddSingleton<ILspNotifier, LspNotifier>();

        services.AddSingleton<LspServerManager>();
        services.AddSingleton<ILspServerManager>(sp => sp.GetRequiredService<LspServerManager>());

        // Language pack system: registry (built-in + user packs), installer (binary setup),
        // and hosted service (auto-starts enabled servers on app startup without blocking).
        services.AddSingleton<LanguagePackRegistry>();
        services.AddSingleton<LanguagePackInstaller>();
        services.AddHostedService<LspHostedService>();

        // Startup hint collector — bridges background services (e.g. LspHostedService) and the TUI:
        // producers push actionable hints (like "Go project detected, install gopls"), the TUI
        // subscribes and displays them in the conversation transcript.
        services.AddSingleton<IStartupHintCollector, StartupHintCollector>();

        return services;
    }
}
