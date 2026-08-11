using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// MAF Agent Middleware: limits tool result sizes to prevent token blow-up.
/// Truncates large string results (> maxResultChars) with a warning.
/// </summary>
public sealed class ToolExecutionBudgetMiddleware
{
    private readonly int _maxResultChars;
    private readonly ILogger<ToolExecutionBudgetMiddleware>? _logger;

    public ToolExecutionBudgetMiddleware(
        int maxResultChars = 150_000,
        ILogger<ToolExecutionBudgetMiddleware>? logger = null)
    {
        _maxResultChars = maxResultChars;
        _logger = logger;
    }

    public Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        CreateDelegate()
    {
        return async (_, ctx, next, ct) =>
        {
            var result = await next(ctx, ct).ConfigureAwait(false);

            if (ctx.Function is null)
                return result;

            var text = result as string;
            if (text is null && result is not null)
            {
                try
                {
                    text = System.Text.Json.JsonSerializer.Serialize(result);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "JSON serialization failed for tool result, falling back to ToString()");
                    text = result.ToString();
                }
            }

            if (text is not null && text.Length > _maxResultChars)
            {
                _logger?.LogDebug(
                    "Tool '{Tool}' result truncated: {Original} -> {Limit} chars",
                    ctx.Function.Name, text.Length, _maxResultChars);

                var safeIndex = FindSafeTruncationIndex(text, _maxResultChars);

                var suffix = LooksLikeJson(text)
                    ? $"\n...[truncated from {text.Length:N0} to {safeIndex:N0} characters; JSON may be incomplete]"
                    : $"\n\n[Tool output truncated from {text.Length:N0} to {safeIndex:N0} characters]";

                return (object)(text[..safeIndex] + suffix);
            }

            return result;
        };
    }

    private static int FindSafeTruncationIndex(string text, int maxChars)
    {
        var safeIndex = Math.Min(maxChars, text.Length);
        if (safeIndex < text.Length && safeIndex > 0 && char.IsHighSurrogate(text[safeIndex - 1]))
            safeIndex--;

        if (!LooksLikeJson(text))
            return safeIndex;

        var boundary = text.LastIndexOfAny(['\n', '\r', ',', '}', ']'], safeIndex - 1, safeIndex);
        return boundary > 0 ? boundary + 1 : safeIndex;
    }

    private static bool LooksLikeJson(string text)
    {
        var span = text.AsSpan().TrimStart();
        return span.StartsWith("{", StringComparison.Ordinal) || span.StartsWith("[", StringComparison.Ordinal);
    }
}
