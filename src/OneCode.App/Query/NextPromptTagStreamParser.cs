using System.Text;

namespace OneCode.App.Query;

/// <summary>
/// Separates the optional next-prompt trailer from a streamed assistant reply.
/// The trailer is requested only after the final answer, allowing one model
/// request to produce both the answer and a TUI suggestion.
/// </summary>
public sealed class NextPromptTagStreamParser
{
    private const string OpenTag = "<onecode-next-prompt>";
    private const string CloseTag = "</onecode-next-prompt>";

    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _suggestion = new();
    private bool _insideSuggestion;

    /// <summary>Processes one streamed chunk into visible text and suggestion segments.</summary>
    public IEnumerable<(string? Text, string? Suggestion)> Process(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            yield break;

        _buffer.Append(chunk);
        var pending = _buffer.ToString();
        _buffer.Clear();

        while (pending.Length > 0)
        {
            if (_insideSuggestion)
            {
                var closeIndex = pending.IndexOf(CloseTag, StringComparison.Ordinal);
                if (closeIndex < 0)
                {
                    var suffixLength = GetPartialSuffixLength(pending, CloseTag);
                    _suggestion.Append(suffixLength == 0 ? pending : pending[..^suffixLength]);
                    if (suffixLength > 0)
                        _buffer.Append(pending[^suffixLength..]);
                    yield break;
                }

                _suggestion.Append(pending[..closeIndex]);
                var suggestion = _suggestion.ToString().Trim();
                _suggestion.Clear();
                _insideSuggestion = false;
                if (suggestion.Length > 0)
                    yield return (null, suggestion);

                pending = pending[(closeIndex + CloseTag.Length)..];
                continue;
            }

            var openIndex = pending.IndexOf(OpenTag, StringComparison.Ordinal);
            if (openIndex < 0)
            {
                var suffixLength = GetPartialSuffixLength(pending, OpenTag);
                if (pending.Length > suffixLength)
                    yield return (pending[..(pending.Length - suffixLength)], null);
                if (suffixLength > 0)
                    _buffer.Append(pending[^suffixLength..]);
                yield break;
            }

            if (openIndex > 0)
                yield return (pending[..openIndex], null);

            _insideSuggestion = true;
            pending = pending[(openIndex + OpenTag.Length)..];
        }
    }

    /// <summary>Emits incomplete markup as text so an interrupted response loses no content.</summary>
    public string? Flush()
    {
        var pending = _buffer.ToString();
        _buffer.Clear();

        if (!_insideSuggestion)
            return pending.Length == 0 ? null : pending;

        _insideSuggestion = false;
        var incomplete = OpenTag + _suggestion + pending;
        _suggestion.Clear();
        return incomplete;
    }

    private static int GetPartialSuffixLength(string text, string tag)
    {
        var maxLength = Math.Min(text.Length, tag.Length - 1);
        for (var length = maxLength; length > 0; length--)
        {
            if (text.AsSpan(text.Length - length).SequenceEqual(tag.AsSpan(0, length)))
                return length;
        }

        return 0;
    }
}
