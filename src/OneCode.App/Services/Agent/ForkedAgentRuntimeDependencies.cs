using Microsoft.Extensions.AI;
using OneCode.Core.Models;

namespace OneCode.App.Services.Agent;

/// <summary>Runtime deps shared by forked / worker agent runs.</summary>
public sealed record ForkedAgentRuntimeDependencies(
    IChatClient ChatClient,
    IModelManager ModelManager,
    IWorkingDirectoryAccessor WorkingDirectory,
    ToolMetadataRegistry ToolMetadata,
    CompactionProviderBuilder CompactionBuilder);
