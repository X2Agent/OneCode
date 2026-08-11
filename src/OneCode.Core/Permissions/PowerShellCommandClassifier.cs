using System.Text.RegularExpressions;

namespace OneCode.Core.Permissions;

public static partial class PowerShellCommandClassifier
{
    private static readonly ShellTokenizer Tokenizer = new(new ShellTokenizerOptions(
        EscapeChar: '`',
        HandleEscapeOutsideQuotes: true,
        SupportsAmpersandRedirect: false,
        NumberRedirectPrefixes: ['1', '2', '*'],
        SupportsDoubleRedirect: true,
        WriteRedirectionTokens: [">", ">>", "1>", "2>", "*>", "1>>", "2>>"]
    ));

    private static readonly Dictionary<string, string> CommandAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ls"] = "get-childitem",
        ["dir"] = "get-childitem",
        ["pwd"] = "get-location",
        ["cd"] = "set-location",
        ["gc"] = "get-content",
        ["cat"] = "get-content",
        ["gi"] = "get-item",
        ["gci"] = "get-childitem",
        ["sls"] = "select-string",
        ["echo"] = "write-output",
        ["ri"] = "remove-item",
        ["rm"] = "remove-item",
        ["del"] = "remove-item",
        ["rd"] = "remove-item",
        ["rmdir"] = "remove-item",
        ["mi"] = "move-item",
        ["mv"] = "move-item",
        ["copy"] = "copy-item",
        ["cp"] = "copy-item",
        ["ren"] = "rename-item",
        ["type"] = "get-content",
        ["curl"] = "invoke-webrequest",
        ["iwr"] = "invoke-webrequest",
        ["irm"] = "invoke-restmethod",
        ["iex"] = "invoke-expression",
    };

    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "get-content", "get-item", "get-childitem", "get-process", "get-service",
        "get-location", "test-path", "resolve-path", "get-command",
        "select-string", "where-object", "foreach-object", "select-object",
        "sort-object", "measure-object", "get-variable", "get-alias", "get-module",
        "get-date", "get-host", "get-psprovider", "get-psdrive", "whoami",
        "hostname", "findstr", "grep", "rg", "git"
    };

    private static readonly HashSet<string> ReadOnlyGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "rev-parse", "branch", "remote", "fetch",
        "blame", "annotate", "shortlog", "describe", "ls-files", "grep"
    };

    private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "remove-item", "clear-content", "set-content", "add-content", "out-file",
        "copy-item", "move-item", "rename-item", "new-item", "expand-archive",
        "invoke-webrequest", "invoke-restmethod", "tee-object", "export-csv",
        "export-clixml", "format-volume", "clear-disk", "set-executionpolicy",
        "disable-windowsoptionalfeature", "invoke-expression", "start-process",
        "stop-process", "stop-service", "restart-service", "restart-computer",
        "robocopy", "tar", "bsdtar"
    };

    private static readonly Dictionary<string, string[]> PathParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["get-content"] = ["-path", "-literalpath"],
        ["get-item"] = ["-path", "-literalpath"],
        ["get-childitem"] = ["-path", "-literalpath"],
        ["test-path"] = ["-path", "-literalpath"],
        ["resolve-path"] = ["-path", "-literalpath"],
        ["select-string"] = ["-path", "-literalpath"],
        ["set-location"] = ["-path", "-literalpath"],
        ["remove-item"] = ["-path", "-literalpath"],
        ["clear-content"] = ["-path", "-literalpath"],
        ["set-content"] = ["-path", "-literalpath"],
        ["add-content"] = ["-path", "-literalpath"],
        ["out-file"] = ["-filepath"],
        ["copy-item"] = ["-path", "-literalpath", "-destination"],
        ["move-item"] = ["-path", "-literalpath", "-destination"],
        ["rename-item"] = ["-path", "-literalpath"],
        ["new-item"] = ["-path", "-name"],
        ["expand-archive"] = ["-path", "-literalpath", "-destinationpath"],
        ["invoke-webrequest"] = ["-outfile"],
        ["invoke-restmethod"] = ["-outfile"],
        ["tee-object"] = ["-filepath"],
        ["export-csv"] = ["-path", "-literalpath"],
        ["export-clixml"] = ["-path", "-literalpath"],
        ["tar"] = ["-f", "--file"],
        ["bsdtar"] = ["-f", "--file"],
        ["robocopy"] = [],
        ["findstr"] = [],
        ["grep"] = [],
        ["rg"] = [],
    };

    // WarningPatterns 是 PowerShellCommandClassifier 独立维护的"危险命令 Warning 提示"模式集，
    // 用于 IsDestructive/GetDestructiveCommandWarning 给用户展示警告（非 Layer 0 硬拦截）。
    //
    // 与 DangerousCommandPatterns.Layer0HardDeny 的区别：
    // - 语义层级不同：本类是权限策略层的 Warning 提示（可被用户审批放行），
    //   Layer0HardDeny 是 Infrastructure 层的硬拦截（BypassPermissions 也生效，不可放行）。
    // - 模式粒度不同：本类模式带 \b 边界、更细粒度，
    //   Layer0HardDeny 模式更具体。
    // - 不应合并：两层独立演进，Warning 提示可以更敏感（多报），硬拦截必须精确（少误杀）。
    private static readonly (Regex Pattern, string Warning)[] WarningPatterns =
    [
        (PsGitResetHardRegex(), "Note: may discard uncommitted changes"),
        (PsGitCleanForceRegex(), "Note: may permanently delete untracked files"),
        (PsGitPushForceRegex(), "Note: may overwrite remote history"),
        (PsGitCommitAmendRegex(), "Note: may rewrite the last commit"),
        (PsRemoveItemRecurseForceRegex(), "Note: may recursively force-remove files"),
        (PsRemoveItemForceRecurseRegex(), "Note: may recursively force-remove files"),
        (PsRemoveItemRecurseRegex(), "Note: may recursively remove files"),
        (PsRemoveItemForceRegex(), "Note: may force-remove files"),
        (PsClearContentStarRegex(), "Note: may clear content of multiple files"),
        (PsSetExecutionPolicyRegex(), "Note: may weaken script execution safeguards"),
        (PsStartProcessRunAsRegex(), "Note: may launch a process with elevated privileges"),
        (PsFormatVolumeClearDiskRegex(), "Note: may destroy disk data"),
        (PsRestartStopComputerRegex(), "Note: may interrupt the current machine"),
    ];

    [GeneratedRegex(@"\bgit\s+reset\s+--hard\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsGitResetHardRegex();

    [GeneratedRegex(@"\bgit\s+clean\b(?![^|;&\n]*(?:-[a-zA-Z]*n|--dry-run))[^|;&\n]*-[a-zA-Z]*f", RegexOptions.IgnoreCase)]
    private static partial Regex PsGitCleanForceRegex();

    [GeneratedRegex(@"\bgit\s+push\b[^|;&\n]*\s(--force|--force-with-lease|-f)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsGitPushForceRegex();

    [GeneratedRegex(@"\bgit\s+commit\b[^|;&\n]*--amend\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsGitCommitAmendRegex();

    [GeneratedRegex(@"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Recurse\b[^|;&\n}]*-Force\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsRemoveItemRecurseForceRegex();

    [GeneratedRegex(@"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Force\b[^|;&\n}]*-Recurse\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsRemoveItemForceRecurseRegex();

    [GeneratedRegex(@"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Recurse\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsRemoveItemRecurseRegex();

    [GeneratedRegex(@"(?:^|[|;&\n({])\s*(Remove-Item|rm|del|rd|rmdir|ri)\b[^|;&\n}]*-Force\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsRemoveItemForceRegex();

    [GeneratedRegex(@"\bClear-Content\b[^|;&\n]*\*", RegexOptions.IgnoreCase)]
    private static partial Regex PsClearContentStarRegex();

    [GeneratedRegex(@"\bSet-ExecutionPolicy\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsSetExecutionPolicyRegex();

    [GeneratedRegex(@"\bStart-Process\b[^|;&\n]*-(Verb|v)(:|\s+)RunAs\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsStartProcessRunAsRegex();

    [GeneratedRegex(@"\b(Format-Volume|Clear-Disk)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsFormatVolumeClearDiskRegex();

    [GeneratedRegex(@"\b(Restart-Computer|Stop-Computer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PsRestartStopComputerRegex();

    private static readonly HashSet<string> OutputCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "write-output", "write-host", "echo"
    };

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

            var commandIndex = SkipPrefixes(statement);
            if (commandIndex >= statement.Count)
                continue;

            var executable = NormalizeCommandName(statement[commandIndex]);
            if (string.Equals(executable, "git", StringComparison.OrdinalIgnoreCase))
            {
                if (!ShellTokenizer.IsReadOnlyGitStatement(statement, commandIndex, ReadOnlyGitSubcommands))
                    return false;
                continue;
            }

            if (OutputCommands.Contains(executable))
                return !ArgumentsLeakValue(statement.Skip(commandIndex + 1));

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

            if (Tokenizer.ContainsWriteRedirection(statement))
                return true;

            var commandIndex = SkipPrefixes(statement);
            if (commandIndex >= statement.Count)
                continue;

            var executable = NormalizeCommandName(statement[commandIndex]);
            if (DangerousCommands.Contains(executable))
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

            if (Tokenizer.ContainsWriteRedirection(statement))
                return "Note: may overwrite files via PowerShell redirection";

            var commandIndex = SkipPrefixes(statement);
            if (commandIndex >= statement.Count)
                continue;

            var executable = NormalizeCommandName(statement[commandIndex]);
            if (string.Equals(executable, "set-content", StringComparison.OrdinalIgnoreCase)
                || string.Equals(executable, "add-content", StringComparison.OrdinalIgnoreCase)
                || string.Equals(executable, "out-file", StringComparison.OrdinalIgnoreCase))
                return "Note: may write file contents";
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

            var commandIndex = SkipPrefixes(statement);
            if (commandIndex >= statement.Count)
                continue;

            var executable = NormalizeCommandName(statement[commandIndex]);
            var args = statement.Skip(commandIndex + 1).ToList();

            paths.AddRange(ExtractRedirectionTargets(args));

            if (string.Equals(executable, "robocopy", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var path in args.Where(IsPathOperand).Take(2))
                    paths.Add(path);
                continue;
            }

            if (string.Equals(executable, "git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(executable, "write-output", StringComparison.OrdinalIgnoreCase)
                || string.Equals(executable, "write-host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PathParameters.TryGetValue(executable, out var parameterNames) && parameterNames.Length > 0)
                paths.AddRange(ExtractParameterPaths(args, parameterNames));

            paths.AddRange(ExtractPositionalPaths(executable, args));
        }

        return paths;
    }

    public static string? GetPrimaryCommandName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        foreach (var statement in Tokenizer.SplitStatements(command))
        {
            if (statement.Count == 0)
                continue;

            var commandIndex = SkipPrefixes(statement);
            if (commandIndex < statement.Count)
                return NormalizeCommandName(statement[commandIndex]);
        }

        return null;
    }

    private static IEnumerable<string> ExtractPositionalPaths(string executable, IReadOnlyList<string> args)
    {
        List<string> candidates = [];
        var filtered = args.Where(IsPathOperand).ToList();
        if (filtered.Count == 0)
            return candidates;

        switch (executable.ToLowerInvariant())
        {
            case "get-content":
            case "get-item":
            case "get-childitem":
            case "test-path":
            case "resolve-path":
            case "select-string":
            case "set-location":
            case "remove-item":
            case "clear-content":
            case "set-content":
            case "add-content":
            case "out-file":
            case "rename-item":
            case "new-item":
            case "export-csv":
            case "export-clixml":
            case "findstr":
            case "grep":
            case "rg":
                candidates.Add(filtered[0]);
                break;
            case "copy-item":
            case "move-item":
            case "expand-archive":
            case "invoke-webrequest":
            case "invoke-restmethod":
            case "tee-object":
                candidates.AddRange(filtered.Take(2));
                break;
        }

        return candidates;
    }

    private static IEnumerable<string> ExtractParameterPaths(IReadOnlyList<string> args, IReadOnlyList<string> parameterNames)
    {
        List<string> paths = [];
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!LooksLikeParameter(arg))
                continue;

            foreach (var parameterName in parameterNames)
            {
                if (!TryMatchParameter(arg, parameterName, out var inlineValue))
                    continue;

                if (!string.IsNullOrWhiteSpace(inlineValue))
                {
                    paths.Add(inlineValue);
                    break;
                }

                if (i + 1 < args.Count && IsPathOperand(args[i + 1]))
                {
                    paths.Add(args[i + 1]);
                    i++;
                }

                break;
            }
        }

        return paths;
    }

    private static IEnumerable<string> ExtractRedirectionTargets(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] is not ">" and not ">>" and not "1>" and not "2>" and not "*>" and not "1>>" and not "2>>")
                continue;

            if (IsPathOperand(args[i + 1]))
                yield return args[i + 1];
        }
    }

    private static bool TryMatchParameter(string token, string parameterName, out string? inlineValue)
    {
        inlineValue = null;
        if (!token.StartsWith(parameterName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (token.Length == parameterName.Length)
            return true;

        if (token[parameterName.Length] != ':')
            return false;

        inlineValue = token[(parameterName.Length + 1)..];
        return true;
    }

    private static bool ArgumentsLeakValue(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (LooksLikeParameter(arg))
                continue;

            if (arg.Contains('$') || arg.Contains("$(", StringComparison.Ordinal) || arg.Contains("@{", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsPathOperand(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || LooksLikeParameter(token))
            return false;

        if (token is ">" or ">>" or "1>" or "2>" or "*>" or "1>>" or "2>>" or "|" or ";")
            return false;

        return !token.StartsWith("$", StringComparison.Ordinal)
            && !token.StartsWith("@", StringComparison.Ordinal)
            && !token.StartsWith("{", StringComparison.Ordinal);
    }

    private static bool LooksLikeParameter(string token) =>
        token.StartsWith("-", StringComparison.Ordinal)
        || token.StartsWith("/", StringComparison.Ordinal);

    private static int SkipPrefixes(IReadOnlyList<string> statement)
    {
        var index = 0;
        while (index < statement.Count)
        {
            var token = statement[index];
            if (token is "&" or ".")
            {
                index++;
                continue;
            }

            if (token.StartsWith("$", StringComparison.Ordinal) && token.Contains('='))
            {
                index++;
                continue;
            }

            if (index + 1 < statement.Count
                && token.StartsWith("$", StringComparison.Ordinal)
                && statement[index + 1] == "=")
            {
                index += 2;
                continue;
            }

            break;
        }

        return index;
    }

    private static string NormalizeCommandName(string token)
    {
        var normalized = token.Trim();
        if (string.IsNullOrEmpty(normalized))
            return normalized;

        var lastSlash = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
        if (lastSlash >= 0)
            normalized = normalized[(lastSlash + 1)..];

        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return CommandAliases.TryGetValue(normalized, out var alias) ? alias : normalized.ToLowerInvariant();
    }
}
