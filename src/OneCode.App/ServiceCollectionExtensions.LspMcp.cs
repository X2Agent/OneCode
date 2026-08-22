using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.Lsp;
using OneCode.App.Services.PlanMode;
using OneCode.Core.Lsp;
using OneCode.Core.Product;
using OneCode.Core.Tasks;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Mcp;
using System.Net.Http.Headers;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection RegisterLspAndMcpServices(this IServiceCollection services)
    {
        services.AddSingleton<PlanModeService>();
        services.AddSingleton<IPlanModeService>(sp => sp.GetRequiredService<PlanModeService>());
        services.AddSingleton<ITaskStore, OneCode.Infrastructure.Tasks.JsonTaskStore>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<ITaskService>(sp => sp.GetRequiredService<TaskService>());
        services.AddSingleton<McpElicitationHandler>(sp =>
            Services.Mcp.ConsoleMcpElicitationHandler.Create(
                sp.GetRequiredService<ILogger<McpElicitationHandler>>()));
        // Smithery MCP registry client (used by /mcp search and /mcp install).
        services.AddSingleton<McpRegistryClient>();
        services.AddSingleton<McpMultiScopeConfigLoader>();
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

        RegisterNamedHttpClients(services);

        return services;
    }

    private static void RegisterNamedHttpClients(IServiceCollection services)
    {
        services.AddHttpClient("OneCode.McpRegistry", client =>
        {
            client.BaseAddress = new Uri(Constants.Urls.McpRegistry);
            client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.McpRegistry);
        }).ConfigurePrimaryHttpMessageHandler(() => CreateProxyAwareHandler());

        services.AddHttpClient("HookHttp", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }).ConfigurePrimaryHttpMessageHandler(() => CreateProxyAwareHandler());

        services.AddHttpClient("WebSearch", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.WebSearch);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneCode", "1.0"));
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.9, */*;q=0.1");
        })
        .ConfigurePrimaryHttpMessageHandler(() => CreateProxyAwareHandler())
        .AddHttpMessageHandler<VcrDelegatingHandler>();

        services.AddHttpClient(Constants.HttpClientNames.Upgrade, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.Default.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        })
            .ConfigurePrimaryHttpMessageHandler(() => CreateProxyAwareHandler());

        services.AddHttpClient("WebFetch", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OneCode/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/markdown, text/html, */*");
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = CreateProxyAwareHandler();
            handler.AllowAutoRedirect = false;
            return handler;
        })
        .AddHttpMessageHandler<VcrDelegatingHandler>();

        services.AddHttpClient(Constants.HttpClientNames.ModelsDev, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.ModelsDev);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneCode", "1.0"));
        }).ConfigurePrimaryHttpMessageHandler(() => CreateProxyAwareHandler());
    }
}
