using OneCode.App.Modes;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Models;

namespace OneCode.WebUI.Services;

/// <summary>Web 会话客户端的依赖分组（避免构造参数膨胀）。</summary>
/// <param name="Runner">会话运行器。</param>
/// <param name="SessionManager">会话存储与生命周期。</param>
/// <param name="ModeController">BUILD/PLAN/GOAL 工作模式控制器。</param>
/// <param name="CommandRegistry">斜杠命令注册表。</param>
/// <param name="BuildSystemPrompt">构建主 agent 系统 prompt（agent 正文，不含 harness 片段）。</param>
/// <param name="LoadHarness">加载共享 harness 片段。</param>
/// <param name="ModelManager">模型目录与选择。</param>
/// <param name="AppState">应用状态访问器。</param>
/// <param name="CreateCommands">
/// 每电路新建 <see cref="SlashCommandPipeline"/> 的工厂：管线持有命令结果缓存与退出标记
/// 等会话内状态，多浏览器电路不得共享同一实例（TUI 单用户场景才用单例）。
/// </param>
/// <param name="Streams">查询流端口（普通查询 / 命令 prompt / 循环 / 工作流恢复）。</param>
/// <param name="BuildRunReplay">BuildRun 历史回放。</param>
public sealed record WebChatDependencies(
    IConversationRunner Runner,
    ISessionManager SessionManager,
    WorkingModeController ModeController,
    ICommandRegistry CommandRegistry,
    Func<CancellationToken, Task<string>> BuildSystemPrompt,
    Func<CancellationToken, Task<string>> LoadHarness,
    IModelManager ModelManager,
    IAppStateAccessor AppState,
    Func<SlashCommandPipeline> CreateCommands,
    IQueryStreamService Streams,
    BuildRunReplayService BuildRunReplay);
