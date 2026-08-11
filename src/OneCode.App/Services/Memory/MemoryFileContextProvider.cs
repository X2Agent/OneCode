// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Context;
using OneCode.Core.Memory;

namespace OneCode.App.Services.Memory;

/// <summary>
/// Exposes a <c>search_memories</c> tool so the LLM can autonomously recall full memory
/// entries from <c>MEMORY.md</c>. Retrieval delegates to <see cref="IMemoryService.FindRelevantMemoriesAsync"/>.
/// </summary>
public sealed class MemoryFileContextProvider : ReadOnlyAIContextProviderBase
{
    private const string SearchToolName = "search_memories";

    private const string SearchToolDescription =
        "Search persistent memory entries (in MEMORY.md) for relevant context. " +
        "Use this when you need to recall user preferences, project conventions, past " +
        "decisions, lessons from failures, or any durable fact. " +
        "Pass a natural-language query describing what you are looking for.";

    private readonly IMemoryService _memoryService;
    private readonly ILogger<MemoryFileContextProvider> _logger;
    private readonly string _workingDirectory;

    public MemoryFileContextProvider(
        IMemoryService memoryService,
        ILogger<MemoryFileContextProvider> logger,
        string workingDirectory)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        AIFunction tool = AIFunctionFactory.Create(SearchMemoriesAsync, SearchToolName, SearchToolDescription);
        return new(new AIContext { Tools = [tool] });
    }

    private async Task<string> SearchMemoriesAsync(
        [Description("Natural-language query describing the information to recall.")]
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "No memories found: empty query.";

        try
        {
            var matches = await _memoryService
                .FindRelevantMemoriesAsync(_workingDirectory, query, cancellationToken)
                .ConfigureAwait(false);

            if (matches.Count == 0)
                return "No relevant memories found.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Found {matches.Count} relevant memor{(matches.Count == 1 ? "y" : "ies")}:");
            foreach (var match in matches)
            {
                var scope = match.Scope == MemoryScope.Project ? "project" : "global";
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"## {match.Entry.Key} ({scope}, score {match.RelevanceScore})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {match.Entry.Source}, Category: {match.Entry.Category}");
                sb.AppendLine(match.Entry.Value.Trim());
            }

            _logger.LogDebug("search_memories returned {Count} result(s) for '{Query}'", matches.Count, query);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_memories failed for '{Query}'", query);
            return $"Memory search failed: {ex.Message}";
        }
    }
}
