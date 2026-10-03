using System.Text.RegularExpressions;
using OneCode.Core.Tools;

namespace OneCode.App.Tui;

/// <summary>展开详情中的一行：文本 + 可选前景色（null 由调用方取默认色）。</summary>
internal sealed record ToolDetailLine(string Text, Color? Fg);

/// <summary>
/// 工具结果展开详情的按工具格式化：先把信封/双重编码解包成工具原始输出
/// （<see cref="DisplayJsonSerializer.TryUnwrapEnvelope"/>），WebSearch 等结构化结果
/// 渲染为人类可读列表，其余工具回退到缩进 JSON。纯函数无状态；
/// 折叠行的单行摘要见 <see cref="ToolResultSummarizer"/>。
/// </summary>
internal static partial class ToolResultDetailFormatter
{
    public static IReadOnlyList<ToolDetailLine> FormatDetail(string toolName, string? rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
            return [new ToolDetailLine("(空结果)", TuiPalette.FgMuted)];

        var content = DisplayJsonSerializer.TryUnwrapEnvelope(rawResult, out var inner)
            ? inner
            : rawResult;

        return ToolResultSummarizer.NormalizeName(toolName) switch
        {
            "websearch" => FormatWebSearch(content),
            _ => FormatGeneric(content),
        };
    }

    /// <summary>
    /// WebSearch 结果 {provider, query, results:[{title,url,snippet}]} →
    /// 头行（query · provider · N 条）+ 每条结果的标题 / URL / 摘要。
    /// 内层解析失败（含上游截断产生的非法 JSON）回退通用展示。
    /// </summary>
    private static IReadOnlyList<ToolDetailLine> FormatWebSearch(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return FormatGeneric(content);

            var root = doc.RootElement;
            List<ToolDetailLine> lines = [new(BuildSearchHeader(root, results), TuiPalette.FgPrimary)];

            var index = 1;
            foreach (var item in results.EnumerateArray())
            {
                // 标题/摘要过 HTML 剥离：Tavily 旧结果（历史转录回放）夹带站点页脚标签。
                var title = HtmlPlainText.Strip(GetStringProperty(item, "title"));
                if (string.IsNullOrEmpty(title))
                    title = "(无标题)";
                lines.Add(new($"{index++}. {title}", TuiPalette.FgPrimary));

                var url = GetStringProperty(item, "url");
                if (!string.IsNullOrEmpty(url))
                    lines.Add(new($"   {url}", TuiPalette.Accent));

                var snippet = HtmlPlainText.Strip(GetStringProperty(item, "snippet"));
                if (!string.IsNullOrEmpty(snippet))
                    lines.Add(new($"   {TruncateText(snippet, SnippetMaxChars)}", TuiPalette.FgSecondary));
            }

            return lines;
        }
        catch (JsonException)
        {
            return FormatGeneric(content);
        }
    }

    private const int SnippetMaxChars = 200;

    private static string BuildSearchHeader(JsonElement root, JsonElement results)
    {
        var header = $"搜索: {GetStringProperty(root, "query") ?? "?"}";
        var provider = GetStringProperty(root, "provider");
        if (!string.IsNullOrEmpty(provider))
            header += $" · {provider}";
        return $"{header} · {results.GetArrayLength()} 条";
    }

    /// <summary>通用回退：NormalizeForDisplay（缩进 JSON / 解码转义的纯文本）按行输出。</summary>
    private static IReadOnlyList<ToolDetailLine> FormatGeneric(string content)
    {
        var display = DisplayJsonSerializer.NormalizeForDisplay(content);
        return display
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => new ToolDetailLine(line, null))
            .ToList();
    }

    private static string? GetStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }

    private static string TruncateText(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
