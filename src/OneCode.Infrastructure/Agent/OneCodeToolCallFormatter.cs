using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Formatter for L1 tool-result folding that keeps each call's arguments and bounds the result body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the default.</b> <see cref="ToolResultCompactionStrategy.DefaultToolCallFormatter"/>
/// groups results by tool <i>name</i> and emits <c>FunctionResultContent.Result.ToString()</c> verbatim.
/// That loses two things a coding session depends on:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Arguments.</b> Without them a folded <c>Read</c> or <c>Grep</c> call no longer says which file
///     or pattern it covered, so the model cannot tell whether a fact it remembers came from the file it
///     is about to change.
///   </description></item>
///   <item><description>
///     <b>A size bound.</b> An unbounded body means a single large tool result can keep occupying the
///     budget the fold was supposed to release.
///   </description></item>
/// </list>
/// <para>
/// The output stays YAML-like so it reads the same way as the default; only the per-call association and
/// the truncation marker are added. Truncation is explicit (<c>[truncated]</c>) rather than silent, so the
/// model knows the body is incomplete instead of assuming it saw everything.
/// </para>
/// </remarks>
public static class OneCodeToolCallFormatter
{
    /// <summary>Maximum characters of a single call's arguments kept in the folded output.</summary>
    public const int MaxArgumentChars = 200;

    /// <summary>Maximum characters of a single call's result kept in the folded output.</summary>
    public const int MaxResultChars = 800;

    /// <summary>Marker appended when a value was cut short.</summary>
    public const string TruncationMarker = "… [truncated]";

    /// <summary>
    /// Renders a tool-call group as a bounded, per-call YAML-like block.
    /// </summary>
    public static string Format(CompactionMessageGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var calls = new List<(string CallId, string Name, string? Arguments)>();
        var resultsByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        var unattributedResults = new List<string>();

        foreach (var message in group.Messages)
        {
            if (message.Contents is null)
                continue;

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        calls.Add((call.CallId, call.Name, SerializeArguments(call)));
                        break;

                    case FunctionResultContent result when result.CallId is not null:
                        resultsByCallId[result.CallId] = result.Result?.ToString() ?? string.Empty;
                        break;
                }
            }

            // Tool-role text with no FunctionResultContent has no call to attach to.
            if (message.Role == ChatRole.Tool
                && message.Text is { Length: > 0 } text
                && !message.Contents.OfType<FunctionResultContent>().Any())
            {
                unattributedResults.Add(text);
            }
        }

        var sb = new StringBuilder("[Tool Calls]");
        var unattributedIndex = 0;

        foreach (var (callId, name, arguments) in calls)
        {
            sb.Append('\n').Append(name).Append(':');

            if (arguments is { Length: > 0 })
                sb.Append("\n  args: ").Append(Bound(arguments, MaxArgumentChars));

            var result = resultsByCallId.TryGetValue(callId, out var matched)
                ? matched
                : unattributedIndex < unattributedResults.Count
                    ? unattributedResults[unattributedIndex++]
                    : null;

            if (!string.IsNullOrEmpty(result))
                sb.Append("\n  result: ").Append(Bound(result, MaxResultChars));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Serializes call arguments compactly. Arguments are untrusted tool input, so the text is passed
    /// through as-is rather than interpreted; only its length is bounded.
    /// </summary>
    private static string? SerializeArguments(FunctionCallContent call)
    {
        if (call.Arguments is not { Count: > 0 } arguments)
            return null;

        return string.Join(", ", arguments.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string Bound(string value, int maxChars) =>
        value.Length <= maxChars
            ? value
            : value[..maxChars] + TruncationMarker;
}
