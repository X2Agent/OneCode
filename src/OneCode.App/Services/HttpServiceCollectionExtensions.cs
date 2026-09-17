using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Product;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services;

/// <summary>
/// 具名 HttpClient 注册——App 层全部出站 HTTP 客户端在此一处配置：统一经
/// <see cref="CreateTransportHandler"/> 应用 mTLS，VCR 录制回放经
/// <c>VcrDelegatingHandler</c> 按需挂载。
/// 消费方一律通过 <c>IHttpClientFactory.CreateClient(Constants.HttpClientNames.Xxx)</c> 取用。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class HttpServiceCollectionExtensions
{
    /// <summary>Creates an HttpClientHandler using platform defaults and mTLS.</summary>
    private static HttpClientHandler CreateTransportHandler()
    {
        var handler = new HttpClientHandler();
        MtlsHelper.ApplyToHandler(handler);
        return handler;
    }

    /// <summary>注册全部具名 HttpClient；由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。</summary>
    public static IServiceCollection AddNamedHttpClients(this IServiceCollection services)
    {
        // MCP 官方注册表（/mcp search、/mcp install）。
        services.AddHttpClient(Constants.HttpClientNames.McpRegistry, client =>
        {
            client.BaseAddress = new Uri(Constants.Urls.McpRegistry);
            client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.McpRegistry);
        });

        // HTTP Hook 执行器（自定义 URL/Method/Headers/Body 的通用 HTTP 调用）。
        services.AddHttpClient(Constants.HttpClientNames.HookHttp, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        // 网页搜索：UA 由 WebSearchTool 按目标站点设置（DuckDuckGo 反爬要求真实浏览器 UA）。
        services.AddHttpClient(Constants.HttpClientNames.WebSearch, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.WebSearch);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.9, */*;q=0.1");
        })
        .AddHttpMessageHandler<VcrDelegatingHandler>();

        // 升级检查与 Release Notes 获取（GitHub API）。
        services.AddHttpClient(Constants.HttpClientNames.Upgrade, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.Default.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        })
            .ConfigurePrimaryHttpMessageHandler(CreateTransportHandler);

        // 网页抓取：禁用自动重定向，由 WebFetchTool 手动控制跳转。
        services.AddHttpClient(Constants.HttpClientNames.WebFetch, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OneCode/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/markdown, text/html, */*");
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = CreateTransportHandler();
            handler.AllowAutoRedirect = false;
            return handler;
        })
        .AddHttpMessageHandler<VcrDelegatingHandler>();

        // models.dev 模型目录客户端（能力元数据刷新）。
        services.AddHttpClient(Constants.HttpClientNames.ModelsDev, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.ModelsDev);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneCode", "1.0"));
        }).ConfigurePrimaryHttpMessageHandler(CreateTransportHandler);

        return services;
    }
}