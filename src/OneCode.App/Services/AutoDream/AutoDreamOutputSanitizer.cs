using System.Text.RegularExpressions;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// 不可信 Agent 输出的清洗纯函数集，防止 MEMORY.md 结构注入：
/// - Key：换行→空格、移除前导 #、长度截断
/// - Value：行首 "## " 改写为 "# # "（破坏 header 模式）、长度截断
/// </summary>
internal static partial class AutoDreamOutputSanitizer
{
    /// <summary>单次整合允许的最大变更条数（防止 Agent 输出风暴）。</summary>
    public const int MaxChangesPerConsolidation = 50;

    /// <summary>Key 最大长度（{category}:{short-id} 格式，100 字符足够描述性 ID）。</summary>
    public const int MaxKeyLength = 100;

    /// <summary>Value 最大长度（10K 字符覆盖多段事实/约定，超出截断）。</summary>
    public const int MaxValueLength = 10_000;

    /// <summary>
    /// 清洗 Agent 输出的 Key，防止 MEMORY.md 结构注入。
    /// - 换行 → 空格：Key 必须单行（<c>OneCode.Infrastructure.Memory.MemoryEntryStore</c> 序列化为 <c>## {Key}</c> 单行 header）
    /// - 前导 <c>#</c> → 移除：防止 <c>## fact:foo</c> 被解析器当作已存在的 header 而错位
    /// - 长度限制：防止超长 Key 撑爆 MEMORY.md 单行
    /// </summary>
    public static string SanitizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        // 统一换行为空格，确保单行
        var single = key.ReplaceLineEndings(" ").Trim();

        // 移除所有前导 '#'（防止 ## 注入）
        single = single.TrimStart('#').TrimStart();

        // 长度截断
        return single.Length > MaxKeyLength ? single[..MaxKeyLength] : single;
    }

    /// <summary>
    /// 清洗 Agent 输出的 Value，防止 MEMORY.md 结构注入。
    /// - 移除行首 <c>## </c>：这些行会被 <c>MemoryEntryStore.EntryHeaderRegex</c>
    ///   误识别为新的 entry header，导致当前 Value 被截断、后续行被解析为独立（无元数据）条目。
    ///   处理方式：将行首 <c>## </c> 替换为 <c># # </c>（保留可读性，破坏 header 模式）。
    /// - 长度限制：防止超长 Value 撑爆 MEMORY.md
    /// </summary>
    public static string SanitizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.Length > MaxValueLength)
            trimmed = trimmed[..MaxValueLength];

        // 防止行首 "## " 注入：逐行检查，将 "## " 替换为 "# # "
        // (RegexOptions.Multiline 使 ^ 匹配每行行首)
        return EntryHeaderInjectionRegex().Replace(trimmed, "# # ");
    }

    [GeneratedRegex(@"^##\s+", RegexOptions.Multiline)]
    private static partial Regex EntryHeaderInjectionRegex();

    public static string? ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        if (start < 0) return null;
        var end = text.LastIndexOf(']');
        if (end <= start) return null;
        return text.Substring(start, end - start + 1);
    }
}
