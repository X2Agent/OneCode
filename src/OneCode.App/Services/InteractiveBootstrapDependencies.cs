using OneCode.App.Query;
using OneCode.App.Session;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;
using IAppStateAccessor = OneCode.Core.Domain.IAppStateAccessor;

namespace OneCode.App.Services;

/// <summary>Session stack for interactive bootstrap.</summary>
public sealed record InteractiveSessionStack(
    IConversationRunner ConversationRunner,
    ISessionManager SessionManager,
    IAppStateAccessor AppState,
    IPermissionModeProvider PermissionMode);

/// <summary>Command/model discovery for interactive bootstrap.</summary>
public sealed record InteractiveDiscoveryDependencies(
    ICommandRegistry CommandRegistry,
    IEnumerable<IDynamicCommandSource> DynamicCommandSources,
    IToolCatalog ToolCatalog,
    IModelManager ModelManager,
    IConfigManager ConfigManager);
