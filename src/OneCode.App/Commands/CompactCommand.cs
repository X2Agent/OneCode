using OneCode.App.Services.Compact;

namespace OneCode.App.Commands;

public sealed class CompactCommand(CompactService compactService) : Command
{
    public override string Name => "compact";
    public override string Description => "Clear history but keep a summary";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ProgressMessage => "Compacting conversation...";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        int? fromIndex = null;
        int? upToIndex = null;
        string? instructions = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--from" or "-f" && i + 1 < args.Length && int.TryParse(args[++i], out var from))
                fromIndex = from;
            else if (args[i] is "--up-to" or "-u" && i + 1 < args.Length && int.TryParse(args[++i], out var upTo))
                upToIndex = upTo;
            else
                instructions = string.Join(" ", args[i..]);
        }

        ct.ThrowIfCancellationRequested();

        var summary = await compactService.CompactAsync(
            customInstructions: instructions,
            fromMessageIndex: fromIndex,
            upToMessageIndex: upToIndex,
            ct: ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(summary))
            return CommandResult.Text("No compaction was needed.");

        return fromIndex.HasValue || upToIndex.HasValue
            ? CommandResult.Text("Partial compaction complete.\nUse /compact to compact the full conversation when ready.")
            : CommandResult.Text("Conversation compacted.\nA summary has been generated from the compacted messages.");
    }
}
