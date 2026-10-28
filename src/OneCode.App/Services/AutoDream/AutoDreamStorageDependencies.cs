using Microsoft.Extensions.AI;
using OneCode.App.Session;
using OneCode.Core.Config;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Tokens;

namespace OneCode.App.Services.AutoDream;

/// <summary>LLM / prompt / memory deps for AutoDream.</summary>
public sealed record AutoDreamAgentDependencies(
    IChatClient ChatClient,
    IModelManager ModelManager,
    IPromptManager PromptManager,
    IMemoryService? MemoryService = null);

/// <summary>Storage / config / session deps for AutoDream.</summary>
/// <remarks><paramref name="TokenLedger"/>：整合 agent 直连 chatClient（不经
/// UsageTrackingRunMiddleware），经此把整合消耗补记入全局账本。</remarks>
public sealed record AutoDreamStorageDependencies(
    IMemoryEntryStore EntryStore,
    IConfigManager ConfigManager,
    IWorkingDirectoryAccessor WorkingDirectory,
    ISessionManager? SessionManager = null,
    ITokenLedger? TokenLedger = null);
