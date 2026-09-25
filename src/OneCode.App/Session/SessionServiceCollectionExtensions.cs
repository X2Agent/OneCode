using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Tools;
using OneCode.Core.Session;
using OneCode.Infrastructure;

namespace OneCode.App.Session;

/// <summary>
/// Session 领域 DI 注册——与会话实现（<see cref="SessionManager"/> / <see cref="EventSourcedSessionStore"/>）
/// 同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class SessionServiceCollectionExtensions
{
    /// <remarks>
    /// <see cref="SessionManager"/> 依赖 <see cref="ISessionToolSetManager"/>（工具域，
    /// 由组合根在 <c>AddToolServices</c> 之后调用本方法保证可用）。
    /// </remarks>
    public static IServiceCollection AddSessionServices(
        this IServiceCollection services, string workingDir)
    {
        services.Configure<SessionOptions>(o => o.InitialWorkingDirectory = workingDir);

        services.AddSingleton<ISessionEventStore>(sp =>
            new FileSessionEventStore(PathsHelper.UserHome));
        services.AddSingleton<ISessionStore>(sp =>
            new EventSourcedSessionStore(
                sp.GetRequiredService<ISessionEventStore>()));
        services.AddSingleton<SessionIdHolder>();
        services.AddSingleton<ISessionIdProvider>(sp => sp.GetRequiredService<SessionIdHolder>());
        services.AddSingleton<IWorkingDirectoryAccessor, SessionWorkingDirectoryAccessor>();

        services.AddSingleton<SessionToolSetManager>();
        services.AddSingleton<ISessionToolSetManager>(sp => sp.GetRequiredService<SessionToolSetManager>());

        services.AddSingleton<SessionManager>(sp =>
        {
            var store = sp.GetRequiredService<ISessionStore>();
            var logger = sp.GetRequiredService<ILogger<SessionManager>>();
            var sessionOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SessionOptions>>().Value;
            return new SessionManager(store, logger, sessionOptions.InitialWorkingDirectory,
                shellExecutorCleanup: sp.GetRequiredService<IShellExecutorCleanup>(),
                tokenUsageTracker: sp.GetRequiredService<Services.Observability.ITokenUsageTracker>(),
                sessionIdHolder: sp.GetRequiredService<SessionIdHolder>(),
                sessionToolSetManager: sp.GetRequiredService<ISessionToolSetManager>());
        });
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionConversationAccess>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionChatHistoryReader>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionWorkingDirectory>(sp => sp.GetRequiredService<SessionManager>());

        return services;
    }
}
