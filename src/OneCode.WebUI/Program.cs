using OneCode.App;
using OneCode.App.Modes;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Models;
using OneCode.App.Services.Interaction;
using OneCode.WebUI.Components;
using OneCode.WebUI.Services;

var builder = WebApplication.CreateBuilder(args);

// 工作目录由配置或进程当前目录显式确定，核心注册不隐式读取。
var configuredWorkingDirectory = builder.Configuration["OneCode:WorkingDirectory"];
if (!string.IsNullOrWhiteSpace(configuredWorkingDirectory))
    Directory.SetCurrentDirectory(configuredWorkingDirectory);

var workingDir = Directory.GetCurrentDirectory();

// 仅绑定 loopback：浏览器通道是 Blazor SignalR，不对外暴露任何 OneCode 端点。
builder.WebHost.UseUrls(builder.Configuration["OneCode:Url"] ?? "http://127.0.0.1:5080");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddSingleton<WebInteractionDispatcher>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddScoped(sp =>
{
    var promptBuilder = sp.GetRequiredService<PromptConfigBuilder>();
    return new WebChatDependencies(
        sp.GetRequiredService<IConversationRunner>(),
        sp.GetRequiredService<ISessionManager>(),
        sp.GetRequiredService<WorkingModeController>(),
        sp.GetRequiredService<ICommandRegistry>(),
        ct => promptBuilder.BuildSystemPromptAsync(ct),
        ct => promptBuilder.LoadHarnessAsync(ct),
        sp.GetRequiredService<IModelManager>(),
        sp.GetRequiredService<IAppStateAccessor>(),
        () => ActivatorUtilities.CreateInstance<SlashCommandPipeline>(sp),
        sp.GetRequiredService<IQueryStreamService>(),
        sp.GetRequiredService<BuildRunReplayService>());
});
builder.Services.AddScoped<ChatSessionClient>();

// 交互端口：前端只提供投递通道，等待/取消/降级由 App 统一实现覆盖无头默认实现。
OneCodeApp.AddOneCodeCore(
    builder.Services,
    workingDir,
    services => services.ReplaceInteractionDispatcher<WebInteractionDispatcher>());

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
