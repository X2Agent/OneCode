using Microsoft.Extensions.AI;
using OneCode.Core.Domain;
using OneCode.Core.Models;

namespace OneCode.App.Services.Agent;

/// <summary>Runtime deps shared by forked / worker agent runs.</summary>
public sealed record ForkedAgentRuntimeDependencies(
    IChatClient ChatClient,
    IModelManager ModelManager,
    IWorkingDirectoryAccessor WorkingDirectory,
    ToolMetadataRegistry ToolMetadata,
    CompactionStrategyFactory CompactionBuilder,
    ISessionIdProvider SessionIds);
