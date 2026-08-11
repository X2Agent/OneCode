namespace OneCode.Infrastructure;

using OneCode.Core;

/// <summary>
/// 基于字符类型加权的 Token 估算器。
/// 不依赖外部 tokenizer 数据包，按字符类别（ASCII / CJK / 其他 Unicode）加权估算。
/// 用于触发 auto-compact 阈值、可观测性统计等场景，精确计费以 API 返回的 Usage 为准。
/// </summary>
public sealed class TokenEstimator : ITokenEstimator
{
    public int EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return EstimateByCharClasses(text);
    }

    public int EstimateTokens(string? text, string? modelId)
    {
        // 字符加权启发式与模型无关，统一估算
        return EstimateTokens(text);
    }

    public (string text, int tokens) TruncateToBudget(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text))
            return (text, 0);

        var tokens = EstimateByCharClasses(text);
        if (tokens <= maxTokens)
            return (text, tokens);

        // 按估算比例截断，留少量余量避免低估
        var ratio = (double)maxTokens / tokens;
        var targetLen = (int)(text.Length * ratio * 0.95);
        if (targetLen >= text.Length)
            return (text, tokens);

        var truncated = text.Substring(0, Math.Max(0, targetLen));
        return (truncated, EstimateByCharClasses(truncated));
    }

    public int EstimateMessageTokens(string systemPrompt, IEnumerable<string> messages)
    {
        int total = EstimateTokens(systemPrompt);
        foreach (var msg in messages)
            total += EstimateTokens(msg);
        return total;
    }

    /// <summary>
    /// 按字符类别加权估算 token 数：
    ///   - ASCII（字母/数字/标点/空白）：~4 chars/token
    ///   - CJK（中日韩）：~1.5 chars/token
    ///   - 其他 Unicode（emoji 等）：~1 char/token
    /// </summary>
    private static int EstimateByCharClasses(string text)
    {
        int ascii = 0;
        int cjk = 0;
        int other = 0;

        foreach (var ch in text)
        {
            if (ch <= 0x7F)
            {
                ascii++;
            }
            else if (IsCjk(ch))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        var asciiTokens = (int)Math.Ceiling(ascii / 4.0);
        var cjkTokens = (int)Math.Ceiling(cjk / 1.5);
        var otherTokens = other;

        return asciiTokens + cjkTokens + otherTokens;
    }

    private static bool IsCjk(char ch)
    {
        var code = (int)ch;
        return
            (code >= 0x4E00 && code <= 0x9FFF) ||
            (code >= 0x3400 && code <= 0x4DBF) ||
            (code >= 0x3040 && code <= 0x309F) ||
            (code >= 0x30A0 && code <= 0x30FF) ||
            (code >= 0xAC00 && code <= 0xD7AF) ||
            (code >= 0x1100 && code <= 0x11FF) ||
            (code >= 0x3130 && code <= 0x318F) ||
            (code >= 0x3000 && code <= 0x303F) ||
            (code >= 0xFF00 && code <= 0xFFEF);
    }
}
