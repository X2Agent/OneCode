using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Context;
using OneCode.App.Services.Memory;
using OneCode.App.Services.Skills;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>Builds shared <see cref="AIContextProvider"/> lists for all agent profiles.</summary>
public sealed class SharedContextProviderBuilder(
    ILoggerFactory loggerFactory,
    SkillProviderHolder skillProviderHolder,
    AgentMemoryDependencies memory,
    AgentRuntimeContextDependencies runtime,
    IModelManager modelManager)
{
    /// <summary>Applies profile-specific defaults before building shared providers.</summary>
    public static AgentContextProviderOptions ApplyProfileDefaults(
        PipelineProfile profile,
        AgentContextProviderOptions options) => profile switch
        {
            PipelineProfile.Full => options,
            PipelineProfile.Worker or PipelineProfile.Explore or PipelineProfile.Plan => options with
            {
                IncludeLspDiagnostics = false,
                IncludeShellEnvironment = false,
            },
            PipelineProfile.TeamMember => options with
            {
                IncludeSessionMemory = false,
                IncludeCodeAct = false,
            },
            _ => options,
        };

    /// <summary>Builds the shared ContextProvider list controlled by <paramref name="options"/> flags.</summary>
    public List<AIContextProvider> BuildCommon(AgentContextProviderOptions options)
    {
        var providers = new List<AIContextProvider>();
        var cwd = options.WorkingDirectory;

        var currentSkillsProvider = skillProviderHolder.Current;
        if (currentSkillsProvider is not null)
            providers.Add(currentSkillsProvider);

        providers.Add(new MemoryFileContextProvider(
            memory.MemoryService,
            loggerFactory.CreateLogger<MemoryFileContextProvider>(),
            cwd));

        if (options.IncludeSessionMemory)
        {
            providers.Add(new SessionMemoryContextProvider(
                memory.SessionMemoryService,
                loggerFactory.CreateLogger<SessionMemoryContextProvider>(),
                memory.SessionManager,
                options.ChatClient!,
                modelManager,
                conversationId: options.ConversationId));
        }

        providers.Add(new DesignContextProvider(
            memory.SessionManager,
            loggerFactory.CreateLogger<DesignContextProvider>(),
            cwd,
            options.ConversationId));

        if (options.IncludeLspDiagnostics)
        {
            providers.Add(new LspDiagnosticContextProvider(
                runtime.LspDiagnosticRegistry,
                loggerFactory.CreateLogger<LspDiagnosticContextProvider>(),
                cwd));
        }

        providers.Add(runtime.TaskContextProvider);

        if (options.IncludeShellEnvironment
            && memory.SessionManager.ForegroundConversation is { } shellConversation
            && runtime.ShellExecutorManager.TryGet(shellConversation.Id) is { } shellExecutor)
        {
            providers.Add(new ShellEnvironmentProvider(shellExecutor));
        }

        if (options.IncludeCodeAct)
        {
            var sandboxFunctions = options.CodeActTools?.OfType<AIFunction>().ToList();
            var codeActProvider = runtime.CodeActService.TryCreateProvider(cwd, sandboxFunctions);
            if (codeActProvider is not null)
                providers.Add(codeActProvider);
        }

        return providers;
    }
}
