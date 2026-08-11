using OneCode.App.Services.Compact;
using OneCode.Core.Coordinator;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;
using IAppStateAccessor = OneCode.Core.Domain.IAppStateAccessor;

namespace OneCode.App.Services.Streaming;

/// <summary>Orchestration backends used by query streaming.</summary>
public sealed record QueryOrchestrationDependencies(
    OrchestrationStreamService OrchestrationStream,
    ITeamOrchestrationService TeamOrchestration,
    AutoCompactService AutoCompact);

/// <summary>App-state / config surfaces for query streaming.</summary>
public sealed record QueryRuntimeDependencies(
    IModelManager ModelManager,
    IAppStateAccessor AppState,
    IConfigManager ConfigManager,
    ThinkingParamsResolver ThinkingParams);
