using System.Text;
using OneCode.App.Session;

namespace OneCode.App.Commands;

public sealed class FilesCommand(ISessionManager sessionManager, ILogger<FilesCommand>? logger = null) : Command
{
    public override string Name => "files";
    public override string Description => "Show files referenced in conversation";
    public override CommandCategory Category => CommandCategory.Builtin;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var conv = sessionManager.ForegroundConversation;
        if (conv is null)
            return Task.FromResult(CommandResult.Error("No active conversation."));

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in conv.Messages.OfType<AssistantMessage>())
        {
            foreach (var block in msg.Content.OfType<ToolUseBlock>())
            {
                try
                {
                    using var doc = JsonDocument.Parse(block.Input);
                    var path = OneCode.Core.Tools.ToolArgumentExtractor.ExtractFilePath(doc.RootElement);
                    if (path is { Length: > 0 })
                        files.Add(path);
                }
                catch (JsonException ex)
                {
                    if (logger is not null)
                        logger.LogDebug(ex, "FilesCommand: tool input is not valid JSON for block {CallId}", block.Id);
                    else
                        System.Diagnostics.Debug.WriteLine($"FilesCommand: tool input not valid JSON: {ex.Message}");
                }
            }
        }

        if (files.Count == 0)
            return Task.FromResult(CommandResult.Text("No files referenced in this conversation."));

        var sb = new StringBuilder($"Files referenced ({files.Count}):\n");
        foreach (var f in files.OrderBy(f => f))
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {f}");
        return Task.FromResult(CommandResult.Text(sb.ToString().TrimEnd()));
    }
}
