using OneCode.App.Services;
using OneCode.App.Services.Streaming;
using OneCode.Core.Config;
using OneCode.Core.Models;
using OneCode.Core.Mcp;
using IAppStateAccessor = OneCode.Core.Domain.IAppStateAccessor;

namespace OneCode.Tui;

/// <summary>Streaming pipeline services for TUI context construction.</summary>
public sealed record TuiStreamingDependencies(
    IQueryStreamService QueryStream,
    SlashCommandPipeline SlashCommands,
    InputQueue InputQueue,
    OneCode.App.Services.RunEventBridge RunEvents);

/// <summary>Catalog / registry surfaces wired into <see cref="TuiContext"/>.</summary>
public sealed record TuiCatalogDependencies(
    IModelManager ModelManager,
    IModelCatalog ModelCatalog,
    IAppStateAccessor AppState,
    // 运行时模型名的兜底来源——会话覆盖（AppState.MainLoopModel）优先于这里的配置有效值
    IConfigManager ConfigManager,
    ICommandRegistry CommandRegistry,
    IToolCatalog ToolCatalog,
    IMcpConnectionManager? McpConnectionManager = null,
    OneCode.App.Services.Mcp.McpStartupPreconnector? Preconnector = null);
