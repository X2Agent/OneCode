using System.Text;

namespace OneCode.App.Query;

/// <summary>
/// Separates the optional next-prompt trailer from a streamed assistant reply.
/// The trailer is requested only after the final answer, allowing one model
/// request to produce both the answer and a TUI suggestion.
/// </summary>
/// <remarks>
/// 只在 <see cref="_carry"/> 上保留「可能是开/闭标签前缀」的未完成尾巴，其余内容即刻产出。
/// 旧实现把每个分片并入缓冲区后整体 <c>ToString()</c>，长回复下每片都复制已累积全文。
/// </remarks>
public sealed class NextPromptTagStreamParser
{
    private const string OpenTag = "<onecode-next-prompt>";
    private const string CloseTag = "</onecode-next-prompt>";

    /// <summary>尚未判定归属的尾部碎片：最多「标签长度 − 1」个字符（跨分片断开的半个开/闭标签前缀）。</summary>
    private readonly StringBuilder _carry = new();
    private readonly StringBuilder _suggestion = new();
    private bool _insideSuggestion;

    /// <summary>Processes one streamed chunk into visible text and suggestion segments.</summary>
    public IEnumerable<(string? Text, string? Suggestion)> Process(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            yield break;

        // 先并入上一分片留下的尾部碎片：它可能是半个开/闭标签，不并入会把跨分片标签拆碎
        // （建议块内的闭标签被拆碎时，碎片会被当成正文吞进建议文本且建议永不闭合）。
        var pending = _carry.Length == 0 ? chunk : string.Concat(_carry, chunk);
        _carry.Clear();

        // 建议块内的内容必须全部攒到闭合标签，无法提前产出
        if (_insideSuggestion)
        {
            foreach (var result in ConsumeInsideSuggestion(pending))
                yield return result;
            yield break;
        }

        var openIndex = pending.IndexOf(OpenTag, StringComparison.Ordinal);
        if (openIndex < 0)
        {
            var suffixLength = GetPartialSuffixLength(pending, OpenTag);
            if (pending.Length > suffixLength)
                yield return (pending[..(pending.Length - suffixLength)], null);
            if (suffixLength > 0)
                _carry.Append(pending, pending.Length - suffixLength, suffixLength);
            yield break;
        }

        if (openIndex > 0)
            yield return (pending[..openIndex], null);

        _insideSuggestion = true;
        foreach (var result in ConsumeInsideSuggestion(pending[(openIndex + OpenTag.Length)..]))
            yield return result;
    }

    /// <summary>Emits incomplete markup as text so an interrupted response loses no content.</summary>
    public string? Flush()
    {
        if (!_insideSuggestion)
        {
            if (_carry.Length == 0)
                return null;

            var remainder = _carry.ToString();
            _carry.Clear();
            return remainder;
        }

        _insideSuggestion = false;
        var incomplete = OpenTag + _suggestion + _carry;
        _suggestion.Clear();
        _carry.Clear();
        return incomplete;
    }

    /// <summary>建议块内的消费：遇到闭标签即产出建议，标签后的其余内容回到可见文本流程。</summary>
    private IEnumerable<(string? Text, string? Suggestion)> ConsumeInsideSuggestion(string text)
    {
        var pending = text;
        if (pending.Length == 0)
            yield break;

        var closeIndex = pending.IndexOf(CloseTag, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            var suffixLength = GetPartialSuffixLength(pending, CloseTag);
            _suggestion.Append(suffixLength == 0 ? pending : pending[..^suffixLength]);
            if (suffixLength > 0)
                _carry.Append(pending, pending.Length - suffixLength, suffixLength);
            yield break;
        }

        _suggestion.Append(pending.AsSpan(0, closeIndex));
        var suggestion = _suggestion.ToString().Trim();
        _suggestion.Clear();
        _insideSuggestion = false;

        if (suggestion.Length > 0)
            yield return (null, suggestion);

        // 闭标签之后按新一段内容重新进入可见文本流程（空建议同样回到可见文本）；
        // rest 比原文短，递归必然收敛。
        foreach (var result in Process(pending[(closeIndex + CloseTag.Length)..]))
            yield return result;
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
