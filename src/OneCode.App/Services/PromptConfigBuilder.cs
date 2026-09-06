using OneCode.Core.Config;
using Microsoft.Agents.AI;
using OneCode.App.Services.Context;
using OneCode.App.Services.Skills;
using OneCode.Core.Memory;
using OneCode.Core.Models;
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

        // MCP 预连接已移出本链路（启动不再被握手阻塞）：由 McpStartupPreconnector 在
        // trust 通过后后台执行；本方法只负责构建提示词 + 首次技能提供者组装，
        // 已连接 MCP 服务器的 skill:// 技能源在其完成后由 RebuildSkillProviderAsync 原子补挂。

        var provider = configManager.Current.Effective.Provider?.ToLowerInvariant();
        var contextWindow = configManager.Current.Effective.OllamaContextWindow;
        var isFiltered = ModelCapabilities.RequiresToolFiltering(provider, contextWindow);
        var availableTools = isFiltered ? BuildAvailableToolsList() : string.Empty;

        var systemPrompt = await BuildDefaultPromptContentAsync(
            systemContext, userContext, memorySection, availableTools, ct).ConfigureAwait(false);

        await RebuildSkillProviderAsync(ct).ConfigureAwait(false);

        return systemPrompt;
    }

    /// <summary>
    /// 重建技能提供者（文件/内置技能 + 已连接 MCP 服务器的 skill:// 技能源）并原子替换。
    /// 系统提示词构建时调用一次；MCP 预连接完成后由 <c>McpStartupPreconnector</c>
    /// 再次调用，把预连接期间缺席的 MCP skills 补挂进 <see cref="SkillProviderHolder"/>。
    /// </summary>
    public async Task RebuildSkillProviderAsync(CancellationToken ct)
    {
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
    }

    /// <summary>
    /// 过滤模式下注入系统提示词的工具清单条目上限（ToolSearch 局部上下文注入）。
    /// MCP 目录可能携带几十个工具，整表灌入会挤占本地小模型的上下文窗口——
    /// 超出部分折叠为一行计数提示，模型仍可通过 ToolSearch 按关键词检索全量工具。
    /// </summary>
    private const int MaxListedTools = 30;

    /// <summary>
    /// 为过滤模式（本地模型）生成紧凑的可用工具列表。
    /// </summary>
    private string BuildAvailableToolsList()
    {
        // 收集全部未加载工具（Contextual + Deferred），按名称排序，超出上限的折叠为计数行。
        var available = toolMetadataRegistry.GetVisibleToolNames()
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(toolMetadataRegistry.Get)
            .Where(static m => m is { LoadPolicy: not ToolLoadPolicy.Always })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Additional tools are available but not loaded. Call them directly to activate, or use ToolSearch to search by keyword.");
        sb.AppendLine();

        var listed = 0;
        foreach (var meta in available)
        {
            if (listed >= MaxListedTools)
                break;

            sb.AppendLine(CultureInfo.InvariantCulture, $"- {meta!.Name}: {meta.SearchHint ?? meta.Name}");
            listed++;
        }

        var omitted = available.Count - listed;
        if (omitted > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- … {omitted} more tools not listed — use ToolSearch to find them.");

        return sb.ToString();
    }
}
