namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Product policy values shared by every summarisation call, so the explicit <c>/compact</c> path
/// and the in-pipeline MAF summarisation strategy produce comparable summaries.
/// </summary>
/// <remarks>
/// The two paths previously diverged: the explicit path passed a <c>ChatOptions</c> with an 8192-token
/// output limit while the MAF strategy called the client with no options at all, leaving the output
/// bound to whatever the provider defaults to. A summary is a bounded artefact — its length must not
/// depend on which compaction path happened to run.
/// </remarks>
public static class SummarizationDefaults
{
    /// <summary>
    /// Upper bound for a single summary response.
    /// </summary>
    /// <remarks>
    /// Summaries are read verbatim by the next turn, so an unbounded response would let one compaction
    /// consume the budget it was meant to free. 8192 is the value the explicit path has always used.
    /// </remarks>
    public const int MaxOutputTokens = 8192;
}
