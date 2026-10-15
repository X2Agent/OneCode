using Microsoft.Extensions.AI;
using OneCode.App.Session;
using OneCode.Core.Config;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;

namespace OneCode.App.Services.AutoDream;

/// <summary>LLM / prompt / memory deps for AutoDream.</summary>
public sealed record AutoDreamAgentDependencies(
    IChatClient ChatClient,
    IModelManager ModelManager,
    IPromptManager PromptManager,
    IMemoryService? MemoryService = null);

/// <summary>Storage / config / session deps for AutoDream.</summary>
public sealed record AutoDreamStorageDependencies(
    IMemoryEntryStore EntryStore,
    IConfigManager ConfigManager,
    IWorkingDirectoryAccessor WorkingDirectory,
    ISessionManager? SessionManager = null);
