using CoreConstants = OneCode.Core.Constants;
using OneCode.App.Session;
using OneCode.Infrastructure;

namespace OneCode.App.Commands;

public sealed class ExportCommand(ISessionManager sessionManager) : Command
{
    public override string Name => "export";
    public override string Description => "Export conversation as JSON";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ArgumentHint => "[--output <path>]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var conv = sessionManager.ForegroundConversation;
        if (conv is null) return CommandResult.Error("No active conversation to export.");

        string? outputPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--output" or "-o" && i + 1 < args.Length) outputPath = args[++i];
        }

        var content = ExportJson(conv);

        outputPath ??= $"conversation-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

        var resolvedPath = Path.GetFullPath(outputPath);
        if (!PathsHelper.IsWithinDirectory(resolvedPath, Directory.GetCurrentDirectory()))
            return CommandResult.Error("Output path must be within the current working directory.");

        await File.WriteAllTextAsync(resolvedPath, content, ct).ConfigureAwait(false);
        return CommandResult.Text($"Conversation exported to: {resolvedPath}");
    }

    private static string ExportJson(Core.Domain.Conversation conv)
    {
        var messages = new List<object>(conv.Messages.Count);
        foreach (var msg in conv.Messages)
        {
            if (msg is UserMessage um)
                messages.Add(new { role = "user", content = um.Content });
            else if (msg is AssistantMessage am)
            {
                var blocks = am.Content.Select<ContentBlock, object>(b => b switch
                {
                    TextBlock tb => new { type = "text", text = tb.Text },
                    ToolUseBlock tub => new { type = "tool_use", id = tub.Id, name = tub.Name, input = tub.Input },
                    _ => new { type = "unknown" }
                }).ToList();
                messages.Add(new { role = CoreConstants.MessageTypes.Assistant, content = blocks });
            }
            else if (msg is ToolResultMessage trm)
                messages.Add(new { role = "tool_result", tool_use_id = trm.ToolUseId, content = trm.Content });
            else if (msg is SystemMessage sys)
                messages.Add(new { role = "system", content = sys.Content });
        }

        var export = new
        {
            id = conv.Id,
            model = conv.Model,
            name = conv.Name,
            created_at = conv.CreatedAt,
            messages
        };

        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }
}
