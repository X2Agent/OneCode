using System.Text;
using System.Text.RegularExpressions;

namespace OneCode.Core.Text;

/// <summary>
/// 统一的检索分词器：拉丁词按大小写边界切分并去复数后缀，CJK 按二字滑窗切分。
/// </summary>
/// <remarks>
/// <para>
/// 记忆检索（<c>MemoryService</c>）与工具检索（<see cref="Tools.ToolRetrievalIndex"/>）共用同一规则。
/// 两条检索路径若各写一套分词，同一个中文查询会得到互相矛盾的召回结果——
/// 中文没有空格分隔，按 <c>\p{L}</c> 取连续串会把整句当成一个 token，任何子串查询都命中不了。
/// </para>
/// <para>
/// 本方法只做分词，不做停用词过滤：调用方按各自政策过滤（工具索引只对 Hint 字段过滤，
/// 记忆检索对所有 token 过滤），过滤后取集合与过滤前取集合结果一致。
/// </para>
/// </remarks>
public static partial class TextTokenizer
{
    /// <summary>
    /// 切分 <paramref name="text"/>。返回值可能包含重复项与同词的不同形态
    /// （如 <c>FindReferences</c> → <c>find</c>、<c>reference</c>、<c>references</c>、<c>findreferences</c>），
    /// 由调用方去重。
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var latin = new StringBuilder();
        var cjk = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushLatin(tokens, latin);
                cjk.Append(ch);
            }
            else if (char.IsLetterOrDigit(ch))
            {
                FlushCjk(tokens, cjk);
                latin.Append(ch);
            }
            else
            {
                FlushLatin(tokens, latin);
                FlushCjk(tokens, cjk);
            }
        }

        FlushLatin(tokens, latin);
        FlushCjk(tokens, cjk);
        return tokens;
    }

    /// <summary>是否 CJK 表意文字（基本区 + 扩展 A）。</summary>
    public static bool IsCjk(char ch)
        => ch is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    private static void FlushLatin(List<string> tokens, StringBuilder latin)
    {
        if (latin.Length == 0)
            return;

        var segment = latin.ToString();
        latin.Clear();

        // PascalCase 切分（"FindReferences" → find / reference(s)），整段小写一并保留（"findreferences"）
        foreach (var part in CamelBoundaryRegex().Split(segment))
        {
            var lower = part.ToLowerInvariant();
            Add(tokens, lower);

            // 简单去复数让 "reference" 与 "references" 互为 exact 匹配
            var stemmed = StripPluralSuffix(lower);
            if (stemmed.Length != lower.Length)
                Add(tokens, stemmed);
        }

        Add(tokens, segment.ToLowerInvariant());
    }

    private static void FlushCjk(List<string> tokens, StringBuilder cjk)
    {
        if (cjk.Length == 0)
            return;

        var segment = cjk.ToString();
        cjk.Clear();

        // 单字保留 unigram（否则 "读" 永远不可达），更长序列按二字滑窗切 bigram
        if (segment.Length == 1)
        {
            Add(tokens, segment);
            return;
        }

        for (var i = 0; i < segment.Length - 1; i++)
            Add(tokens, segment.Substring(i, 2));
    }

    private static void Add(List<string> tokens, string token)
    {
        if (token.Length > 0)
            tokens.Add(token);
    }

    private static string StripPluralSuffix(string word)
        // "ss" 结尾（class/access）的 s 不是复数后缀，不能去
        => word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word[..^1]
            : word;

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundaryRegex();
}
