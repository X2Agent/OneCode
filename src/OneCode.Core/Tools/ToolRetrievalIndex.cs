using System.Text;
using System.Text.RegularExpressions;

namespace OneCode.Core.Tools;

/// <summary>
/// 工具检索命中结果。
/// </summary>
public sealed record ToolSearchMatch(string ToolName, double Score);

/// <summary>
/// 工具检索倒排索引——为本地模型的工具分层选择
/// （<see cref="ToolMetadataRegistry.SelectToolsForLocalModel"/>）与 ToolSearch 工具
/// 提供统一的相关度评分，替代原先的裸子串匹配。
/// </summary>
/// <remarks>
/// PR-17 评估：简化为「精确匹配 + 别名 + 子串兜底」(~50 行) 会回归
/// ContextualScoreThreshold 语义（IDF 抑制 "plan" 误命中 "planetary"、CJK bigram 分词），
/// 且无独立 ToolSearch 命中率测试可锁定行为——暂保留完整评分模型。
/// </remarks>
public sealed partial class ToolRetrievalIndex
{
    // 字段权重：名称/别名/关键词命中比描述命中更能代表用户意图
    private const double NameFieldWeight = 3.0;
    private const double AliasFieldWeight = 2.5;
    private const double KeywordFieldWeight = 2.5;
    private const double HintFieldWeight = 1.0;

    // 匹配分级：exact 才代表用户明确指向该工具，prefix/substring 只是弱信号
    private const double ExactMatchLevel = 1.0;
    private const double PrefixMatchLevel = 0.6;
    private const double SubstringMatchLevel = 0.3;

    // 短 token 的 prefix/substring 噪音极大（如 "ls" 会 prefix 命中 "lsp"），设最小长度门槛
    private const int MinPrefixTokenLength = 2;
    private const int MinSubstringTokenLength = 3;

    // 同义词扩展 token 的权重衰减——扩展词只是语义猜测，不如原词确定
    private const double SynonymWeightFactor = 0.8;

    // Hint 字段中的高频虚词不进入索引（区分度为零且虚增 df）
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "to", "of", "in", "for", "on", "with", "and", "or", "at", "by", "via",
    };

    private readonly Dictionary<string, ToolDocument> _docs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _synonymMap = new(StringComparer.Ordinal);

    /// <summary>
    /// 构建索引。<paramref name="synonymGroups"/> 为可选的等价词分组（如 ["search", "find", "查找"]），
    /// 查询时同组词互为扩展（权重按 <see cref="SynonymWeightFactor"/> 衰减）。
    /// </summary>
    public ToolRetrievalIndex(IEnumerable<IReadOnlyList<string>>? synonymGroups = null)
    {
        if (synonymGroups is null)
            return;

        foreach (var group in synonymGroups)
        {
            var tokens = group
                .SelectMany(g => Tokenize(g, isHint: false))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var token in tokens)
                _synonymMap[token] = tokens;
        }
    }

    /// <summary>注册或替换一个工具的索引文档。</summary>
    public void AddOrUpdate(ToolMetadata meta)
    {
        Remove(meta.Name);

        var doc = new ToolDocument(
            BuildField([meta.Name], isHint: false),
            BuildField(meta.Aliases, isHint: false),
            BuildField(meta.Keywords, isHint: false),
            BuildField([meta.SearchHint ?? ""], isHint: true));
        _docs[meta.Name] = doc;

        foreach (var token in doc.AllTokens())
        {
            if (!_postings.TryGetValue(token, out var set))
                _postings[token] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(meta.Name);
        }
    }

    /// <summary>移除一个工具的索引文档（同时修复倒排表，避免 df 失真）。</summary>
    public void Remove(string toolName)
    {
        if (!_docs.Remove(toolName, out var doc))
            return;

        foreach (var token in doc.AllTokens())
        {
            if (!_postings.TryGetValue(token, out var set))
                continue;
            set.Remove(toolName);
            if (set.Count == 0)
                _postings.Remove(token);
        }
    }

    /// <summary>清空索引。</summary>
    public void Clear()
    {
        _docs.Clear();
        _postings.Clear();
    }

    /// <summary>
    /// 计算单个工具对查询的相关度评分；0 表示完全不相关。
    /// </summary>
    public double Score(string toolName, string query)
    {
        if (!_docs.TryGetValue(toolName, out var doc) || string.IsNullOrWhiteSpace(query))
            return 0;

        double score = 0;
        foreach (var (token, weight) in ExpandSynonyms(Tokenize(query, isHint: false)))
        {
            var best = Math.Max(
                MatchLevel(doc.Name, token) * NameFieldWeight,
                Math.Max(
                    MatchLevel(doc.Alias, token) * AliasFieldWeight,
                    Math.Max(
                        MatchLevel(doc.Keyword, token) * KeywordFieldWeight,
                        MatchLevel(doc.Hint, token) * HintFieldWeight)));

            // df=0 的 token 不会命中任何字段（best=0），天然跳过，无需特判
            if (best > 0)
                score += Idf(token) * weight * best;
        }
        return score;
    }

    /// <summary>
    /// 全量搜索：返回所有评分 &gt; 0 的工具，按评分降序（同分按名称排序保证确定性）。
    /// 条数截断由调用方决定——ToolSearch 需先做可见性过滤，截断发生在过滤之后。
    /// </summary>
    public IReadOnlyList<ToolSearchMatch> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return _docs.Keys
            .Select(name => new ToolSearchMatch(name, Score(name, query)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.ToolName, StringComparer.Ordinal)
            .ToList();
    }

    private double Idf(string token)
    {
        var df = _postings.TryGetValue(token, out var set) ? set.Count : 0;
        return Math.Log((_docs.Count + 1.0) / (df + 1.0)) + 1.0;
    }

    private static double MatchLevel(FieldTokens field, string token)
    {
        if (field.Tokens.Contains(token))
            return ExactMatchLevel;
        if (token.Length >= MinPrefixTokenLength
            && field.Tokens.Any(t => t.StartsWith(token, StringComparison.Ordinal)))
            return PrefixMatchLevel;
        if (token.Length >= MinSubstringTokenLength
            && field.JoinedText.Contains(token, StringComparison.Ordinal))
            return SubstringMatchLevel;
        return 0;
    }

    private List<(string Token, double Weight)> ExpandSynonyms(List<string> tokens)
    {
        var expanded = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            expanded[token] = 1.0;
            if (!_synonymMap.TryGetValue(token, out var group))
                continue;
            foreach (var synonym in group)
                expanded.TryAdd(synonym, SynonymWeightFactor);
        }
        return expanded.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static FieldTokens BuildField(IReadOnlyList<string> texts, bool isHint)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
            foreach (var token in Tokenize(text, isHint))
                tokens.Add(token);

        // substring 检查针对小写原文（如 "FindReferences" → "findreferences"），
        // 若用 token 拼接会丢失跨词边界信息
        return new FieldTokens(tokens, string.Join(' ', texts.Select(t => t.ToLowerInvariant())));
    }

    private static List<string> Tokenize(string text, bool isHint)
    {
        var tokens = new List<string>();
        var latin = new StringBuilder();
        var cjk = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushLatin(tokens, latin, isHint);
                cjk.Append(ch);
            }
            else if (char.IsLetterOrDigit(ch))
            {
                FlushCjk(tokens, cjk);
                latin.Append(ch);
            }
            else
            {
                FlushLatin(tokens, latin, isHint);
                FlushCjk(tokens, cjk);
            }
        }
        FlushLatin(tokens, latin, isHint);
        FlushCjk(tokens, cjk);
        return tokens;
    }

    private static void FlushLatin(List<string> tokens, StringBuilder latin, bool isHint)
    {
        if (latin.Length == 0)
            return;

        var segment = latin.ToString();
        latin.Clear();

        // PascalCase 切分（"FindReferences" → find/reference(s)），整段小写一并保留（"findreferences"）
        foreach (var part in CamelBoundaryRegex().Split(segment))
        {
            var lower = part.ToLowerInvariant();
            AddToken(tokens, lower, isHint);

            // 简单去复数让 "reference" 与 "references" 互为 exact 匹配
            var stemmed = StripPluralSuffix(lower);
            if (stemmed.Length != lower.Length)
                AddToken(tokens, stemmed, isHint);
        }
        AddToken(tokens, segment.ToLowerInvariant(), isHint);
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
            tokens.Add(segment);
            return;
        }
        for (var i = 0; i < segment.Length - 1; i++)
            tokens.Add(segment.Substring(i, 2));
    }

    private static void AddToken(List<string> tokens, string token, bool isHint)
    {
        if (token.Length == 0)
            return;
        if (isHint && StopWords.Contains(token))
            return;
        tokens.Add(token);
    }

    private static string StripPluralSuffix(string word)
        // "ss" 结尾（class/access）的 s 不是复数后缀，不能去
        => word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word[..^1]
            : word;

    private static bool IsCjk(char ch)
        => ch is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundaryRegex();

    private sealed record FieldTokens(HashSet<string> Tokens, string JoinedText);

    private sealed record ToolDocument(FieldTokens Name, FieldTokens Alias, FieldTokens Keyword, FieldTokens Hint)
    {
        public IEnumerable<string> AllTokens()
            => Name.Tokens.Concat(Alias.Tokens).Concat(Keyword.Tokens).Concat(Hint.Tokens);
    }
}
