namespace OneCode.App.Services.Memory;

/// <summary>
/// Session-scoped factual memory merge/extract service.
/// </summary>
public interface ISessionMemoryService
{
    Task<IReadOnlyList<SessionMemoryEntry>> MergeExtractedMemoriesAsync(
        Conversation conversation,
        IEnumerable<string> candidateMemories,
        string source = "auto",
        CancellationToken ct = default);

    /// <summary>Reads session memories stored on the conversation metadata.</summary>
    IReadOnlyList<SessionMemoryEntry> GetMemories(Conversation conversation);

    /// <summary>Heuristic extraction of durable facts from recent messages.</summary>
    IReadOnlyList<string> ExtractKeyFacts(Conversation conversation, int maxFacts = 5);
}
