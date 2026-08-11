namespace OneCode.App.Services.Compact;

/// <summary>
/// Shared constants for the compaction subsystem.
/// </summary>
public static class CompactConstants
{
    /// <summary>
    /// Messages at the end of the conversation that are never collapsed / snipped / cleared.
    /// </summary>
    public const int ProtectedTailSize = 10;

    /// <summary>
    /// Number of trailing messages retained verbatim after a full compact
    /// (boundary marker + summary are inserted before them).
    /// </summary>
    public const int RecentMessagesToKeep = 8;

    /// <summary>Minimum number of user/assistant messages before compaction is considered worthwhile.</summary>
    public const int MinSignificantMessagesForCompact = 4;

    /// <summary>
    /// Boundary marker inserted as a SystemMessage at the start of the compacted history.
    /// Subsequent compactions skip past this marker when selecting retained messages.
    /// </summary>
    public const string CompactBoundaryContent =
        "[Conversation history compacted. The summary below replaces the full history.]";

}
