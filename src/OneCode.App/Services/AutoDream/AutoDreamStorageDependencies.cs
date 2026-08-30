using OneCode.Core.Config;
using Microsoft.Extensions.AI;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;

namespace OneCode.App.Services.AutoDream;

/// <summary>LLM / tool / prompt deps for AutoDream.</summary>
public sealed record AutoDreamAgentDependencies(
    IChatClient ChatClient,
    IToolCatalog ToolCatalog,
    IModelManager ModelManager,
    IPromptManager PromptManager);

/// <summary>Storage / config deps for AutoDream.</summary>
public sealed record AutoDreamStorageDependencies(
    IMemoryEntryStore EntryStore,
    IConfigManager ConfigManager,
    IWorkingDirectoryAccessor WorkingDirectory);
