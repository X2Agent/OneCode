using OneCode.App.Session;
using OneCode.Core.IO;

namespace OneCode.App.Commands;

public sealed class CopyCommand(ISessionManager sessionManager, IClipboardService clipboard) : Command
{
    public override string Name => "copy";
    public override string Description => "Copy response to clipboard";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[N]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var assistantMessages = sessionManager.ForegroundConversation?.Messages
            .OfType<AssistantMessage>()
            .ToList() ?? [];

        if (assistantMessages.Count == 0)
            return CommandResult.Error("No assistant response to copy.");

        // /copy N — select Nth response from the end (1-based); default is last
        var indexFromEnd = 1;
        if (args.Length > 0 && int.TryParse(args[0], out var n) && n >= 1)
            indexFromEnd = n;

        if (indexFromEnd > assistantMessages.Count)
            return CommandResult.Error(
                $"Only {assistantMessages.Count} response(s) available. /copy 1..{assistantMessages.Count}");

        var target = assistantMessages[^indexFromEnd];
        var text = string.Join("\n", target.Content.OfType<TextBlock>().Select(b => b.Text));
        if (string.IsNullOrWhiteSpace(text))
            return CommandResult.Error("Selected response has no text content.");

        var error = await clipboard.TryCopyTextAsync(text, ct).ConfigureAwait(false);
        return error is null
            ? CommandResult.Text(indexFromEnd == 1
                ? "Last response copied to clipboard."
                : $"Response #{indexFromEnd} from end copied to clipboard.")
            : CommandResult.Error($"Failed to copy: {error}");
    }
}
