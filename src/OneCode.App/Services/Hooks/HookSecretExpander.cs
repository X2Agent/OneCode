using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 配置敏感字段展开器（C1）——webhookUrl / secret / url 等字段支持两种保密形式：
/// <list type="bullet">
/// <item><c>dpapi:</c> 前缀：值为 DPAPI(CurrentUser) 保护的 Base64 串，运行时解密
/// （Windows 专属；生成方式见 docs/hooks.md §5.2）。</item>
/// <item><c>${ENV_VAR}</c>：展开进程环境变量；未定义的变量展开为空串。</item>
/// </list>
/// 解密失败抛 <see cref="CryptographicException"/>，由执行器统一转为 NonBlockingError。
/// </summary>
public static partial class HookSecretExpander
{
    private const string DpapiPrefix = "dpapi:";

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex EnvVarRegex();

    /// <summary>展开敏感字段值：dpapi: 前缀解密，否则做 ${ENV_VAR} 展开；null/空原样返回。</summary>
    public static string? Expand(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            var protectedBytes = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }

        return EnvVarRegex().Replace(value, static match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? string.Empty);
    }
}
