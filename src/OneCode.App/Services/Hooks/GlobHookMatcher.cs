using System.Text.RegularExpressions;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Glob 风格通配符匹配器
///
/// 匹配规则：
/// - "" 或 "*" → 匹配所有（wildcard）
/// - "Bash" → 精确匹配（大小写不敏感）
/// - "Bash*" → 通配符匹配（* 匹配任意字符序列）
/// - "Write|Read" → 管道分隔多值匹配（匹配任意一个）
/// </summary>
public sealed partial class GlobHookMatcher
{
    public bool Matches(string pattern, string actualValue)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return true;

        if (string.IsNullOrEmpty(actualValue))
            return false;

        if (pattern.Contains('|'))
        {
            return pattern.Split('|')
                .Any(p => MatchesSingle(p.Trim(), actualValue));
        }

        return MatchesSingle(pattern, actualValue);
    }

    private static bool MatchesSingle(string pattern, string actualValue)
    {
        if (pattern == "*")
            return true;

        if (pattern.Contains('*'))
        {
            var parts = pattern.Split('*');
            if (parts.Length == 2)
            {
                var prefix = parts[0];
                var suffix = parts[1];
                return actualValue.Length >= prefix.Length + suffix.Length
                    && actualValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && actualValue.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            }
            // 更复杂的通配符模式，回退到简单实现
            var regex = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*") + "$";
            return Regex.IsMatch(
                actualValue, regex, RegexOptions.IgnoreCase);
        }

        return string.Equals(pattern, actualValue, StringComparison.OrdinalIgnoreCase);
    }
}
