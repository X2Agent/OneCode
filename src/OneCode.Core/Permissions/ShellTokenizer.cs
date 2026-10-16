using System.Text;

namespace OneCode.Core.Permissions;

internal sealed record ShellTokenizerOptions(
    char EscapeChar,
    bool HandleEscapeOutsideQuotes,
    bool SupportsAmpersandRedirect,
    char[] NumberRedirectPrefixes,
    bool SupportsDoubleRedirect,
    string[] WriteRedirectionTokens
);

internal sealed class ShellTokenizer(ShellTokenizerOptions options)
{
    private readonly HashSet<string> _writeRedirectionSet = new(options.WriteRedirectionTokens, StringComparer.Ordinal);

    internal List<string> Tokenize(string command)
    {
        List<string> tokens = [];
        var current = new StringBuilder();
        var quote = '\0';

        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];

            if (quote != '\0')
            {
                if (ch == options.EscapeChar && i + 1 < command.Length)
                {
                    current.Append(command[++i]);
                    continue;
                }

                if (ch == quote)
                {
                    quote = '\0';
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (options.HandleEscapeOutsideQuotes && ch == options.EscapeChar && i + 1 < command.Length)
            {
                current.Append(command[++i]);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                Flush();
                if (ch == '\n')
                    tokens.Add("\n");
                continue;
            }

            if (ch is ';' or '|')
            {
                Flush();
                if (ch == '|' && i + 1 < command.Length && command[i + 1] == '|')
                {
                    tokens.Add("||");
                    i++;
                }
                else
                {
                    tokens.Add(ch.ToString());
                }
                continue;
            }

            if (ch == '&')
            {
                if (options.SupportsAmpersandRedirect)
                {
                    Flush();
                    if (i + 1 < command.Length && command[i + 1] == '&')
                    {
                        tokens.Add("&&");
                        i++;
                    }
                    else if (i + 1 < command.Length && command[i + 1] == '>')
                    {
                        tokens.Add("&>");
                        i++;
                    }
                    else
                    {
                        current.Append(ch);
                    }
                }
                else
                {
                    if (i + 1 < command.Length && command[i + 1] == '&')
                    {
                        Flush();
                        tokens.Add("&&");
                        i++;
                    }
                    else
                    {
                        current.Append(ch);
                    }
                }
                continue;
            }

            if (ch == '>')
            {
                Flush();
                if (i + 1 < command.Length && command[i + 1] == '>')
                {
                    tokens.Add(">>");
                    i++;
                }
                else
                {
                    tokens.Add(">");
                }
                continue;
            }

            if (Array.IndexOf(options.NumberRedirectPrefixes, ch) >= 0
                && i + 1 < command.Length && command[i + 1] == '>')
            {
                Flush();
                if (options.SupportsDoubleRedirect && i + 2 < command.Length && command[i + 2] == '>')
                {
                    tokens.Add($"{ch}>>");
                    i += 2;
                }
                else
                {
                    tokens.Add($"{ch}>");
                    i++;
                }
                continue;
            }

            current.Append(ch);
        }

        Flush();
        return tokens;
    }

    internal List<List<string>> SplitStatements(string command)
    {
        var tokens = Tokenize(command);
        List<List<string>> statements = [];
        List<string> current = [];

        foreach (var token in tokens)
        {
            if (token is ";" or "&&" or "||" or "|" or "\n")
            {
                if (current.Count > 0)
                    statements.Add(current);
                current = [];
                continue;
            }

            current.Add(token);
        }

        if (current.Count > 0)
            statements.Add(current);

        return statements;
    }

    internal bool ContainsWriteRedirection(IReadOnlyList<string> statement)
    {
        for (var i = 0; i < statement.Count; i++)
        {
            if (!_writeRedirectionSet.Contains(statement[i]))
                continue;

            if (i + 1 < statement.Count && IsNullOrStreamRedirectionTarget(statement[i + 1]))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// 识别标准流丢弃或流复制目标（如 <c>2&gt;/dev/null</c>、<c>2&gt;$null</c>、<c>2&gt;&amp;1</c>），
    /// 避免将常规错误静默重定向误判为破坏性文件覆盖写入。
    /// </summary>
    private static bool IsNullOrStreamRedirectionTarget(string target) =>
        string.Equals(target, "/dev/null", StringComparison.OrdinalIgnoreCase)
        || string.Equals(target, "$null", StringComparison.OrdinalIgnoreCase)
        || string.Equals(target, "nul", StringComparison.OrdinalIgnoreCase)
        || string.Equals(target, "&1", StringComparison.Ordinal)
        || string.Equals(target, "&2", StringComparison.Ordinal);

    internal static bool IsReadOnlyGitStatement(IReadOnlyList<string> statement, int commandIndex, IReadOnlySet<string> readOnlyGitSubcommands)
    {
        if (commandIndex + 1 >= statement.Count)
            return false;

        var subcommand = statement[commandIndex + 1];
        if (!readOnlyGitSubcommands.Contains(subcommand))
            return false;

        if (string.Equals(subcommand, "branch", StringComparison.OrdinalIgnoreCase))
            return statement.Skip(commandIndex + 2).All(arg => arg.StartsWith('-'));

        return !statement.Any(arg => string.Equals(arg, "--amend", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--force-with-lease", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-f", StringComparison.OrdinalIgnoreCase));
    }
}
