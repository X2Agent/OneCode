using Microsoft.Agents.AI;
using OneCode.App.Services.Context;
using OneCode.App.Services.Skills;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;
using System.Text;

namespace OneCode.App.Services;

public sealed class PromptConfigBuilder(
    ILogger<PromptConfigBuilder> logger,
    IConfigManager configManager,
    IMemoryService memoryService,
    ContextBuilder contextBuilder,
    PromptRuntimeDependencies runtimeDeps,
    PromptComposer promptComposer,
    ToolMetadataRegistry toolMetadataRegistry)
{
    /// <summary>
    /// Builds the default system prompt by composing shared harness with
    /// <c>system/default.prompt</c> and injecting runtime context sections.
    /// Throws if either prompt file is unavailable — the three-layer store
    /// (project &gt; user &gt; built-in) guarantees built-in copies are shipped via csproj.
    /// </summary>
    public Task<string> BuildDefaultPromptContentAsync(
        string systemContext,
        string userContext,
        string? memorySection,
        string? availableTools,
        CancellationToken ct) =>
        promptComposer.ComposeMainAsync(systemContext, userContext, memorySection, availableTools, ct);

    /// <summary>
    /// Builds the runtime system prompt and bootstraps MCP + skills.
    /// Callers inject <see cref="Query.ChatService"/> / <see cref="Query.IConversationRunner"/> separately —
    /// this builder no longer returns a conversation runner (breaks the PromptConfigBuilder ↔ ChatService cycle).
    /// </summary>
    /// <remarks>
    /// Memory strategy: <paramref name="memoryQuery"/> should remain null to avoid token duplication.
    /// System prompt only includes entrypoint index (MEMORY.md) so the LLM knows what's available.
    /// Detailed topic retrieval is handled on-demand by <c>MemoryFileContextProvider.search_memories</c> tool.
    /// Memory and user context are injected once via default.prompt placeholders — do not append again.
    /// </remarks>
    public async Task<string> BuildSystemPromptAsync(
        string? memoryQuery,
        CancellationToken ct)
    {
        var memorySection = await memoryService
            .LoadMemoryPromptAsync(Environment.CurrentDirectory, memoryQuery, ct).ConfigureAwait(false);

        var systemContext = await contextBuilder.BuildSystemContextAsync(
            Environment.CurrentDirectory, ct).ConfigureAwait(false);
        var additionalDirs = configManager.GetSetting<string[]>("allowedDirectories");
        var userContext = await contextBuilder.BuildUserContextAsync(
            Environment.CurrentDirectory, additionalDirs, ct).ConfigureAwait(false);

        var provider = configManager.Current.Effective.Provider?.ToLowerInvariant();
        var contextWindow = configManager.Current.Effective.OllamaContextWindow;
        var isFiltered = ModelCapabilities.RequiresToolFiltering(provider, contextWindow);
        var availableTools = isFiltered ? BuildAvailableToolsList() : string.Empty;

        var systemPrompt = await BuildDefaultPromptContentAsync(
            systemContext, userContext, memorySection, availableTools, ct).ConfigureAwait(false);

        await runtimeDeps.McpConnectionManager.ConnectAllAsync(ct).ConfigureAwait(false);

        try
        {
            var builder = new AgentSkillsProviderBuilder();
            AgentSkillsProviderFactory.ConfigureFileAndBundledSkills(
                builder, runtimeDeps.SkillCatalog);
            await runtimeDeps.McpSkillsIntegrator.ApplyAsync(builder, ct).ConfigureAwait(false);
            runtimeDeps.SkillProviderHolder.Replace(builder.Build());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to rebuild AgentSkillsProvider with MCP skills");
        }

        return systemPrompt;
    }

    /// <summary>
    /// 为过滤模式（本地模型）生成紧凑的可用工具列表。
    /// </summary>
    private string BuildAvailableToolsList()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Additional tools are available but not loaded. Call them directly to activate, or use ToolSearch to search by keyword.");
        sb.AppendLine();

        foreach (var name in toolMetadataRegistry.GetVisibleToolNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            var meta = toolMetadataRegistry.Get(name);
            if (meta is null || meta.LoadPolicy == ToolLoadPolicy.Always)
                continue;

            sb.AppendLine(CultureInfo.InvariantCulture, $"- {meta.Name}: {meta.SearchHint ?? meta.Name}");
        }

        return sb.ToString();
    }
}
