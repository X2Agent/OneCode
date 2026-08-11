using OneCode.App.Services.Context;
using OneCode.App.Services.Lsp;
using OneCode.App.Services.Memory;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Memory;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>Memory + session providers for shared agent context.</summary>
public sealed record AgentMemoryDependencies(
    IMemoryService MemoryService,
    ISessionMemoryService SessionMemoryService,
    ISessionManager SessionManager);

/// <summary>Shell / CodeAct / LSP / task providers for shared agent context.</summary>
public sealed record AgentRuntimeContextDependencies(
    ConversationShellExecutorManager ShellExecutorManager,
    IHyperlightCodeActService CodeActService,
    LspDiagnosticRegistry LspDiagnosticRegistry,
    TaskContextProvider TaskContextProvider);
