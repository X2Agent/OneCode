using OneCode.Core.Config;
using OneCode.App.Services.Context;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using System.Text;

namespace OneCode.App.Services;

public sealed class PromptConfigBuilder(
    IConfigManager configManager,
    IMemoryService memoryService,
    ContextBuilder contextBuilder,
    PromptComposer promptComposer,
    ToolMetadataRegistry toolMetadataRegistry)
{
    /// <summary>
    /// Builds the Main agent body (harness fragment excluded — MAF composes it).
    /// Throws if either prompt file is unavailable — the three-layer store
    /// (project &gt; user &gt; built-in) guarantees built-in copies are shipped via csproj.
    /// </summary>
    public Task<string> BuildDefaultPromptContentAsync(
        string systemContext,
        string userContext,
        string? memorySection,
        string? availableTools,
        CancellationToken ct) =>
        promptComposer.RenderMainBodyAsync(systemContext, userContext, memorySection, availableTools, ct);

    /// <summary>
    /// Loads the shared harness fragment (<c>system/harness</c>) for the paths that hand it to
    /// MAF as <c>HarnessAgentOptions.HarnessInstructions</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fragment is loaded alongside the body, not derived from it: MAF composes the two halves
    /// (harness first, then the body) and needs them as separate inputs. Pre-joining them would
    /// duplicate the fragment, because the body is also fed to the model through the transcript.
    /// </para>
    /// <para>
    /// Throws when the file is missing from every store — the same fail-fast policy as
    /// <see cref="BuildSystemPromptAsync"/>. Returning null here would silently fall back to MAF's
    /// generic default instructions and drop the product's security guidance.
    /// </para>
    /// </remarks>
    public Task<string> LoadHarnessAsync(CancellationToken ct) =>
        promptComposer.GetHarnessAsync(ct);

    /// <summary>
    /// Builds the runtime system prompt and bootstraps MCP + skills.
    /// Callers inject <see cref="Query.ChatService"/> / <see cref="Query.IConversationRunner"/> separately —
    /// this builder no longer returns a conversation runner (breaks the PromptConfigBuilder ↔ ChatService cycle).
    /// </summary>
    /// <remarks>
    /// Memory strategy: the system prompt only includes the MEMORY.md entrypoint index so the LLM
    /// knows what's available. Detailed topic retrieval is handled on-demand by the
    /// <c>search_memories</c> tool (MAF TextSearchProvider).
    /// Memory and user context are injected once via default.prompt placeholders — do not append again.
    /// </remarks>
    public async Task<string> BuildSystemPromptAsync(CancellationToken ct)
    {
        var memorySection = await memoryService
            .LoadMemoryPromptAsync(ct).ConfigureAwait(false);

        var systemContext = await contextBuilder.BuildSystemContextAsync(
            Environment.CurrentDirectory, ct).ConfigureAwait(false);
        var additionalDirs = configManager.GetSetting<string[]>("allowedDirectories");
        var userContext = await contextBuilder.BuildUserContextAsync(
            Environment.CurrentDirectory, additionalDirs, ct).ConfigureAwait(false);

        // MCP 预连接已移出本链路（启动不再被握手阻塞）：由 McpStartupPreconnector 在
        // trust 通过后后台执行。技能源在每次 agent run 时解析，因此预连接期间缺席的
        // MCP skills 会在其连上后自动出现，无需重建 provider。

        var provider = configManager.Current.Effective.Provider?.ToLowerInvariant();
        var contextWindow = configManager.Current.Effective.OllamaContextWindow;
        var isFiltered = ModelCapabilities.RequiresToolFiltering(provider, contextWindow);
        var availableTools = isFiltered ? BuildAvailableToolsList() : string.Empty;

        return await BuildDefaultPromptContentAsync(
            systemContext, userContext, memorySection, availableTools, ct).ConfigureAwait(false);
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
