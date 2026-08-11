namespace OneCode.Core.Permissions.Yolo;

/// <summary>
/// In-memory YOLO rule engine (match / mutate). Persistence lives in Infrastructure.
/// </summary>
public sealed class YoloRuleStore
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(2);

    private readonly List<UserRule> _rules = new();
    /// <summary>
    /// Cache compiled regexes to avoid recompilation on every MatchRule call.
    /// Keyed by rule pattern string. Invalidated when rules are reloaded.
    /// </summary>
    private readonly Dictionary<string, System.Text.RegularExpressions.Regex> _compiledRegexCache = new(StringComparer.Ordinal);
    private readonly ILogger<YoloRuleStore>? _logger;
    private readonly object _lock = new();

    // 构造时加载内置默认规则作为初始状态，避免启动竞态期间规则集为空。
    // Automation 的 YoloRuleStoreLoader 启动后会用磁盘规则（或内置默认）替换。
    public YoloRuleStore(ILogger<YoloRuleStore>? logger = null)
    {
        _logger = logger;
        _rules.AddRange(GetBuiltInDefaultRules());
    }

    public IReadOnlyList<UserRule> Rules
    {
        get
        {
            lock (_lock)
            {
                return _rules.ToList().AsReadOnly();
            }
        }
    }

    public UserRule? MatchRule(string command)
    {
        lock (_lock)
        {
            foreach (var rule in _rules)
            {
                try
                {
                    if (!_compiledRegexCache.TryGetValue(rule.Pattern, out var regex))
                    {
                        regex = new System.Text.RegularExpressions.Regex(
                            rule.Pattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
                            matchTimeout: RegexMatchTimeout);
                        _compiledRegexCache[rule.Pattern] = regex;
                    }

                    if (regex.IsMatch(command))
                        return rule;
                }
                catch (System.Text.RegularExpressions.RegexParseException ex)
                {
                    _logger?.LogWarning(ex, "Invalid regex pattern in rule: {Pattern}", rule.Pattern);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
                {
                    _logger?.LogWarning(ex, "Regex timeout in rule: {Pattern} - possible ReDoS attempt", rule.Pattern);
                }
            }
        }
        return null;
    }

    public void AddRule(UserRule rule)
    {
        lock (_lock)
        {
            _rules.Add(rule);
        }
    }

    /// <summary>
    /// Atomically replace the entire rule set (used after loading from disk).
    /// </summary>
    public void ReplaceRules(IReadOnlyList<UserRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_lock)
        {
            _rules.Clear();
            _compiledRegexCache.Clear();
            _rules.AddRange(rules);
        }
    }

    /// <summary>
    /// 清空所有规则。主要用于单元测试场景（隔离用户规则与内置默认规则）。
    /// 生产代码应通过 <see cref="ReplaceRules"/> 重新装载规则，而不是直接清空。
    /// </summary>
    public void ClearRules()
    {
        lock (_lock)
        {
            _rules.Clear();
            _compiledRegexCache.Clear();
        }
    }

    public bool RemoveRule(string pattern)
    {
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _rules.RemoveAt(index);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 内置默认 YOLO 规则集——随包分发，作为用户未配置时的安全兜底。
    /// </summary>
    public static List<UserRule> GetBuiltInDefaultRules()
    {
        var rules = new List<UserRule>
        {
            new("deny", DangerousCommandPatterns.GitForcePush,
                "Block force push to any remote — overwrites remote history, irreversible"),
            new("deny", DangerousCommandPatterns.GitResetHard,
                "Block hard reset to previous commits — destroys uncommitted work"),
            new("deny", DangerousCommandPatterns.GitCleanForce,
                "Block force clean — permanently deletes untracked files"),
            new("deny", DangerousCommandPatterns.GitConfigGlobal,
                "Block global git config changes — can inject malicious hooks"),

            new("deny", DangerousCommandPatterns.RmRfRoot,
                "Block rm -rf on root or home directory"),
            new("deny", DangerousCommandPatterns.GlobalChmod777,
                "Block global chmod 777 — security disaster"),

            new("deny", DangerousCommandPatterns.PipeToShell,
                "Block curl|sh pattern — arbitrary remote code execution"),
            new("deny", DangerousCommandPatterns.PowerShellRemoteScript,
                "Block PowerShell remote script execution"),

            new("deny", DangerousCommandPatterns.DatabaseDrop,
                "Block destructive database operations"),
            new("deny", DangerousCommandPatterns.KubectlDelete,
                "Block destructive kubectl operations"),
            new("deny", DangerousCommandPatterns.TerraformDestroy,
                "Block terraform destroy — tears down infrastructure"),

            new("deny", DangerousCommandPatterns.DiskOverwrite,
                "Block raw disk overwrite via dd"),

            new("deny", DangerousCommandPatterns.ScanCryptoKeys,
                "Block scanning for cryptographic key material — credential theft pattern"),
            new("deny", DangerousCommandPatterns.GrepPrivateKeys,
                "Block scanning for private key headers — credential theft pattern"),
            new("deny", DangerousCommandPatterns.ReadSystemCredentials,
                "Block reading system credential files — privilege escalation reconnaissance"),
            new("deny", DangerousCommandPatterns.ArchiveSensitiveDirs,
                "Block archiving sensitive credential directories — exfiltration pattern"),
            new("deny", DangerousCommandPatterns.CopySensitiveDirs,
                "Block copying sensitive credential directories — exfiltration pattern"),
            new("deny", DangerousCommandPatterns.PipeEnvToEncoder,
                "Block piping environment variables through encoders — credential exfiltration"),
            new("deny", DangerousCommandPatterns.ExportSecretEnvVars,
                "Block explicitly exporting known secret env vars — credential exposure"),

            new("deny", DangerousCommandPatterns.InterpreterSystemCall,
                "Block interpreter -c execution of system commands — obfuscated code execution"),
            new("deny", DangerousCommandPatterns.EvalSystemCall,
                "Block eval/Function constructors with system calls — code injection"),
            new("deny", DangerousCommandPatterns.TwoStepRemoteExec,
                "Block two-step remote execution (download then execute)"),
            new("deny", DangerousCommandPatterns.CurlToDevTcp,
                "Block curl to /dev/tcp — reverse shell pattern"),
            new("deny", DangerousCommandPatterns.Base64PipeToInterpreter,
                "Block base64-decoded pipe to interpreter — obfuscated code execution"),
            new("deny", DangerousCommandPatterns.ReverseShellDevice,
                "Block /dev/tcp and /dev/udp device files — reverse shell / network exfiltration"),

            new("soft_deny", @"(env|printenv)(\s|$)",
                "Soft-deny env var dumps — may leak API keys / secrets"),
            new("soft_deny", @"(history|cat\s+~/\.bash_history|cat\s+~/\.zsh_history)",
                "Soft-deny shell history access — may contain secrets typed by user"),

            new("allow", @"^git\s+(status|diff|log|show|branch|stash\s+list)",
                "Allow read-only git inspection"),
            new("allow", @"^ls(\s|$)",
                "Allow directory listing"),
            new("allow", @"^(cat|head|tail|less|more)\s",
                "Allow file reading"),
            new("allow", @"^grep\s",
                "Allow grep search"),
            new("allow", @"^find\s+\.\s",
                "Allow find in current directory (relative, not full disk)"),
            new("allow", @"^(dotnet|npm|yarn|pnpm|cargo|go|gradle|mvn)\s+(build|test|run|check|fmt|lint)",
                "Allow common build/test/run commands"),
            new("allow", @"^git\s+(add|commit|pull|fetch|merge|rebase|stash\s+pop|checkout\s+\S)",
                "Allow common non-destructive git operations"),
        };
        return rules;
    }
}
