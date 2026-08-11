namespace OneCode.Core.Permissions;

/// <summary>
/// 危险命令模式的单一事实源——所有安全检查层（BashCommandInvariant、YoloRuleStore、
/// PlanContentSafetyScanner）应引用此处的规范模式，不得各自维护独立正则。
///
/// 使用者：
/// - <c>BashCommandInvariant</c>（Infrastructure Layer 0）：用 GeneratedRegex 编译这些模式做硬拦截
/// - <c>YoloRuleStore</c>（Core Auto 模式）：构建 deny UserRule 做权限分类
/// - <c>CreatePlanTool</c>（App 层）：扫描计划内容中的破坏性命令
/// </summary>
public static class DangerousCommandPatterns
{
    // 不可逆 Git 操作

    public const string GitForcePush =
        @"git\s+push\s+(-[a-zA-Z]*f|--force|--force-with-lease)";

    public const string GitResetHard =
        @"git\s+reset\s+--hard\s+HEAD~";

    public const string GitCleanForce =
        @"git\s+clean\s+-[a-zA-Z]*f";

    public const string GitConfigGlobal =
        @"git\s+config\s+--global";

    // 破坏性文件操作

    public const string RmRfRoot =
        @"rm\s+(-[a-zA-Z]*[rf][a-zA-Z]*\s+)+(/|\$HOME|~)";

    public const string GlobalChmod777 =
        @"chmod\s+(-R\s+)?777\s+/";

    // 远程脚本执行

    public const string PipeToShell =
        @"(curl|wget)\s+[^\|]*\|\s*(bash|sh|zsh|powershell)";

    public const string PowerShellRemoteScript =
        @"(iex|invoke-expression)\s*\(\s*(iwr|irm|invoke-webrequest|invoke-restmethod)";

    public const string Base64PipeToInterpreter =
        @"base64\s+(-d|--decode)\s*[|>&].*(bash|sh|zsh|python|perl)";

    // 磁盘 / 基础设施破坏

    public const string DiskOverwrite =
        @"dd\s+.*of\s*=\s*/dev/sd";

    public const string FormatDisk =
        @"mkfs\.";

    public const string DatabaseDrop =
        @"(DROP|TRUNCATE)\s+(TABLE|DATABASE|SCHEMA)";

    public const string KubectlDelete =
        @"kubectl\s+delete\s+(ns|namespace|deploy|deployment|svc|service|pvc|pv)";

    public const string TerraformDestroy =
        @"terraform\s+destroy";

    // 凭证扫描 / 外泄

    public const string ScanCryptoKeys =
        @"(find|locate)\s+.*\.(key|pem|p12|pfx|kdbx|keystore)";

    public const string GrepPrivateKeys =
        @"(grep|rg|findstr)\s+.*BEGIN\s+(RSA|EC|OPENSSH|PRIVATE)";

    public const string ReadSystemCredentials =
        @"(cat|type|get-content)\s+.*(/etc/passwd|/etc/shadow|/etc/sudoers)";

    public const string ArchiveSensitiveDirs =
        @"(tar|zip|7z|rar)\s+.*(~?/\.ssh|~?/\.gnupg|~?/\.aws|~?/\.config/gcloud|~?/\.kube)";

    public const string CopySensitiveDirs =
        @"(cp|copy-item|robocopy)\s+.*(~?/\.ssh|~?/\.gnupg|~?/\.aws)";

    public const string ExportSecretEnvVars =
        @"(export\s+)?(AWS_SECRET_ACCESS_KEY|GITHUB_TOKEN|OPENAI_API_KEY|ANTHROPIC_API_KEY)\s*=";

    public const string PipeEnvToEncoder =
        @"(env|printenv|set)\s*[|>&].*(base64|xxd|od)";

    // 混淆执行 / 反弹 shell

    public const string InterpreterSystemCall =
        @"(python|python3|node|ruby|perl)\s+-c\s+.*(os\.system|subprocess|exec|spawn|child_process)";

    public const string EvalSystemCall =
        @"\b(eval|Function\()\s*\(.*\b(system|exec|spawn|fork)\b";

    public const string TwoStepRemoteExec =
        @"(curl|wget)\s+.*\.(sh|bash|zsh|ps1)\s*[>;|&].*(bash|sh|zsh|powershell|pwsh)";

    public const string ReverseShellDevice =
        @"/dev/tcp/|/dev/udp/";

    public const string CurlToDevTcp =
        @"(curl|wget)\s+[^\|]*>\s*/dev/(tcp|udp)/";

    // fork bomb: :(){ :|:& };:
    // 修复：原模式 [^}]*\|\s*& 要求 | 后直接是 &，但标准 fork bomb 中 | 和 & 之间是函数调用 ":"。
    // 改为 [^}]*\|[^}]*& 允许 | 和 & 之间有非 } 字符，同时保持对 ":(){ ...|...&... };" 结构的精确匹配。
    public const string ForkBomb =
        @":\s*\(\s*\)\s*\{[^}]*\|[^}]*&[^}]*\}\s*;";

    public const string FullDiskScan =
        @"find\s+/";

    public const string PackageInstall =
        @"(npm\s+install|yarn\s+add|pnpm\s+add|pip\s+install|go\s+get)\s+\S+";

    // 集合辅助

    /// <summary>
    /// Layer 0 硬拦截模式（BashCommandInvariant 使用）。
    /// 这些模式在任何权限模式下都生效，包括 BypassPermissions。
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Pattern, string Description)> Layer0HardDeny =
    [
        ("RmRfRoot", RmRfRoot, "Block rm -rf on root/home directory"),
        ("ForkBomb", ForkBomb, "Block fork bomb patterns"),
        ("PipeToShell", PipeToShell, "Block curl|sh arbitrary remote code execution"),
        ("DiskOverwrite", DiskOverwrite, "Block raw disk overwrite via dd"),
        ("FormatDisk", FormatDisk, "Block mkfs disk formatting"),
        ("GlobalChmod777", GlobalChmod777, "Block global chmod 777"),
        ("GitForcePush", GitForcePush, "Block force push to any remote"),
        ("GitResetHard", GitResetHard, "Block hard reset to previous commits"),
        ("PowerShellRemoteScript", PowerShellRemoteScript, "Block PowerShell remote script execution"),
        ("Base64PipeToInterpreter", Base64PipeToInterpreter, "Block base64-decoded pipe to interpreter"),
        ("PackageInstall", PackageInstall, "Block package install with postinstall scripts"),
        ("GitConfigGlobal", GitConfigGlobal, "Block global git config changes"),
        ("FullDiskScan", FullDiskScan, "Block full disk scan from root"),
    ];
}
