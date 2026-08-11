using System.Text.RegularExpressions;

namespace OneCode.Core.Permissions;

public static partial class BashCommandClassifier
{
    private static readonly ShellTokenizer Tokenizer = new(new ShellTokenizerOptions(
        EscapeChar: '\\',
        HandleEscapeOutsideQuotes: false,
        SupportsAmpersandRedirect: true,
        NumberRedirectPrefixes: ['1', '2'],
        SupportsDoubleRedirect: false,
        WriteRedirectionTokens: [">", ">>", "1>", "2>", "&>"]
    ));

    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "pwd", "ls", "dir", "cat", "head", "tail", "grep", "rg", "find",
        "which", "whereis", "basename", "dirname", "wc", "stat", "du", "df",
        "sort", "uniq", "cut", "readlink", "realpath", "env", "printenv",
        "git", "echo"
    };

    private static readonly HashSet<string> ReadOnlyGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "rev-parse", "branch", "remote",
        "blame", "annotate", "shortlog", "describe", "ls-files", "grep"
    };

    private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "mv", "cp", "install", "chmod", "chown", "ln", "touch",
        "mkdir", "dd", "mkfs", "shutdown", "reboot", "halt", "poweroff",
        "sudo", "su", "passwd", "kubectl", "terraform", "sed"
    };

    // WarningPatterns 是 BashCommandClassifier 独立维护的"危险命令 Warning 提示"模式集，
    // 用于 IsDestructive/GetDestructiveCommandWarning 给用户展示警告（非 Layer 0 硬拦截）。
    //
    // 与 DangerousCommandPatterns.Layer0HardDeny 的区别：
    // - 语义层级不同：本类是权限策略层的 Warning 提示（可被用户审批放行），
    //   Layer0HardDeny 是 Infrastructure 层的硬拦截（BypassPermissions 也生效，不可放行）。
    // - 模式粒度不同：本类模式带 \b 边界、更细粒度（如 git reset --hard 不限定 HEAD~），
    //   Layer0HardDeny 模式更具体（如 git reset --hard HEAD~ 才拦截）。
    // - 不应合并：两层独立演进，Warning 提示可以更敏感（多报），硬拦截必须精确（少误杀）。
    private static readonly (Regex Pattern, string Warning)[] WarningPatterns =
    [
        (GitResetHardRegex(), "Note: may discard uncommitted changes"),
        (GitPushForceRegex(), "Note: may overwrite remote history"),
        (GitCleanForceRegex(), "Note: may permanently delete untracked files"),
        (GitCheckoutDotRegex(), "Note: may discard all working tree changes"),
        (GitRestoreDotRegex(), "Note: may discard all working tree changes"),
        (GitStashDropClearRegex(), "Note: may permanently remove stashed changes"),
        (GitBranchForceDeleteRegex(), "Note: may force-delete a branch"),
        (GitNoVerifyRegex(), "Note: may skip safety hooks"),
        (GitCommitAmendRegex(), "Note: may rewrite the last commit"),
        (RmRecursiveForceRegex(), "Note: may recursively force-remove files"),
        (RmRecursiveRegex(), "Note: may recursively remove files"),
        (RmForceRegex(), "Note: may force-remove files"),
        (DropTruncateRegex(), "Note: may drop or truncate database objects"),
        (DeleteFromRegex(), "Note: may delete all rows from a database table"),
        (KubectlDeleteRegex(), "Note: may delete Kubernetes resources"),
        (TerraformDestroyRegex(), "Note: may destroy Terraform infrastructure")
    ];

    [GeneratedRegex(@"\bgit\s+reset\s+--hard\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitResetHardRegex();

    [GeneratedRegex(@"\bgit\s+push\b[^;&|\n]*[ \t](--force|--force-with-lease|-f)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitPushForceRegex();

    [GeneratedRegex(@"\bgit\s+clean\b(?![^;&|\n]*(?:-[a-zA-Z]*n|--dry-run))[^;&|\n]*-[a-zA-Z]*f", RegexOptions.IgnoreCase)]
    private static partial Regex GitCleanForceRegex();

    [GeneratedRegex(@"\bgit\s+checkout\s+(--\s+)?\.[ \t]*($|[;&|\n])", RegexOptions.IgnoreCase)]
    private static partial Regex GitCheckoutDotRegex();

    [GeneratedRegex(@"\bgit\s+restore\s+(--\s+)?\.[ \t]*($|[;&|\n])", RegexOptions.IgnoreCase)]
    private static partial Regex GitRestoreDotRegex();

    [GeneratedRegex(@"\bgit\s+stash[ \t]+(drop|clear)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitStashDropClearRegex();

    [GeneratedRegex(@"\bgit\s+branch\s+(-D[ \t]|--delete\s+--force|--force\s+--delete)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitBranchForceDeleteRegex();

    [GeneratedRegex(@"\bgit\s+(commit|push|merge)\b[^;&|\n]*--no-verify\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitNoVerifyRegex();

    [GeneratedRegex(@"\bgit\s+commit\b[^;&|\n]*--amend\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitCommitAmendRegex();

    [GeneratedRegex(@"(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*[rR][a-zA-Z]*f|(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*f[a-zA-Z]*[rR]", RegexOptions.IgnoreCase)]
    private static partial Regex RmRecursiveForceRegex();

    [GeneratedRegex(@"(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*[rR]", RegexOptions.IgnoreCase)]
    private static partial Regex RmRecursiveRegex();

    [GeneratedRegex(@"(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*f", RegexOptions.IgnoreCase)]
    private static partial Regex RmForceRegex();

    [GeneratedRegex(@"\b(DROP|TRUNCATE)\s+(TABLE|DATABASE|SCHEMA)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DropTruncateRegex();

    [GeneratedRegex(@"\bDELETE\s+FROM\s+\w+[ \t]*(;|""|'|\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteFromRegex();

    [GeneratedRegex(@"\bkubectl\s+delete\b", RegexOptions.IgnoreCase)]
    private static partial Regex KubectlDeleteRegex();

    [GeneratedRegex(@"\bterraform\s+destroy\b", RegexOptions.IgnoreCase)]
    private static partial Regex TerraformDestroyRegex();

    public static bool IsReadOnly(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        foreach (var statement in Tokenizer.SplitStatements(command))
        {
            if (statement.Count == 0)
                continue;

            if (Tokenizer.ContainsWriteRedirection(statement))
                return false;

            var commandIndex = SkipWrappers(statement);
            if (commandIndex >= statement.Count)
                continue;

            var executable = statement[commandIndex];
            if (string.Equals(executable, "git", StringComparison.OrdinalIgnoreCase))
            {
                if (!ShellTokenizer.IsReadOnlyGitStatement(statement, commandIndex, ReadOnlyGitSubcommands))
                    return false;
                continue;
            }

            if (string.Equals(executable, "sed", StringComparison.OrdinalIgnoreCase) && HasSedInPlaceEdit(statement, commandIndex))
                return false;

            if (!ReadOnlyCommands.Contains(executable))
                return false;
        }

        return true;
    }

    public static bool IsDestructive(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (GetDestructiveCommandWarning(command) != null)
            return true;

        foreach (var statement in Tokenizer.SplitStatements(command))
        {
            if (statement.Count == 0)
                continue;

            var commandIndex = SkipWrappers(statement);
            if (commandIndex >= statement.Count)
                continue;

            var executable = statement[commandIndex];
            if (DangerousCommands.Contains(executable))
                return true;

            if (string.Equals(executable, "sed", StringComparison.OrdinalIgnoreCase) && HasSedInPlaceEdit(statement, commandIndex))
                return true;
        }

        return false;
    }

    public static string? GetDestructiveCommandWarning(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        foreach (var (pattern, warning) in WarningPatterns)
        {
            if (pattern.IsMatch(command))
                return warning;
        }

        foreach (var statement in Tokenizer.SplitStatements(command))
        {
            if (statement.Count == 0)
                continue;

            var commandIndex = SkipWrappers(statement);
            if (commandIndex >= statement.Count)
                continue;

            if (HasSedInPlaceEdit(statement, commandIndex))
                return "Note: may modify files in place";

            if (Tokenizer.ContainsWriteRedirection(statement))
                return "Note: may overwrite files via shell redirection";
        }

        return null;
    }

    public static IReadOnlyList<string> ExtractReferencedPaths(string? command)
    {
        List<string> paths = [];
        if (string.IsNullOrWhiteSpace(command))
            return paths;

        foreach (var statement in Tokenizer.SplitStatements(command))
        {
            if (statement.Count == 0)
                continue;

            var commandIndex = SkipWrappers(statement);
            if (commandIndex >= statement.Count)
                continue;

            var executable = statement[commandIndex];
            var args = statement.Skip(commandIndex + 1).ToList();
            switch (executable.ToLowerInvariant())
            {
                case "rm":
                case "rmdir":
                case "mv":
                case "cp":
                case "install":
                case "chmod":
                case "chown":
                case "ln":
                case "touch":
                case "mkdir":
                case "cat":
                case "head":
                case "tail":
                    paths.AddRange(args.Where(IsPathOperand));
                    break;

                case "sed":
                    paths.AddRange(ExtractSedTargetPaths(args));
                    break;
            }
        }

        return paths;
    }

    private static bool HasSedInPlaceEdit(IReadOnlyList<string> statement, int commandIndex)
    {
        for (var i = commandIndex + 1; i < statement.Count; i++)
        {
            var arg = statement[i];
            if (arg == "-i" || arg.StartsWith("-i", StringComparison.Ordinal) || arg.StartsWith("--in-place", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractSedTargetPaths(IReadOnlyList<string> args)
    {
        var inPlace = false;
        var index = 0;
        while (index < args.Count)
        {
            var arg = args[index];
            if (arg == "-i" || arg.StartsWith("-i", StringComparison.Ordinal) || arg.StartsWith("--in-place", StringComparison.Ordinal))
            {
                inPlace = true;
                index++;
                if (arg == "-i" && index < args.Count && !LooksLikeSedProgram(args[index]) && !args[index].StartsWith('-'))
                    index++;
                continue;
            }

            if (arg is "-e" or "-f")
            {
                index += 2;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                index++;
                continue;
            }

            break;
        }

        if (index < args.Count)
            index++;

        return inPlace
            ? args.Skip(index).Where(IsPathOperand)
            : Array.Empty<string>();
    }

    private static bool LooksLikeSedProgram(string token) =>
        token.Contains('s') || token.Contains('d') || token.Contains('p') || token.Contains('a') || token.Contains('c');

    private static bool IsPathOperand(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-'))
            return false;

        if (token is ">" or ">>" or "1>" or "2>" or "&>" or "<" or "<<")
            return false;

        return !token.StartsWith('$') && !token.StartsWith("`", StringComparison.Ordinal);
    }

    private static int SkipWrappers(IReadOnlyList<string> statement)
    {
        var index = 0;
        while (index < statement.Count)
        {
            var token = statement[index];
            if (string.Equals(token, "sudo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "env", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "command", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "nohup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "time", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (token.Contains('=') && !token.StartsWith("./", StringComparison.Ordinal) && !token.StartsWith("../", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }
}

