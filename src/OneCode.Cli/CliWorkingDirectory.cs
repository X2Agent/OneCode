namespace OneCode.Cli;

/// <summary>
/// 解析并应用全局 <c>--cwd</c>/<c>-C</c> 选项：在快路径与 DI 容器创建之前切换进程工作目录，
/// 使 OneCode 以指定目录为工作区启动（类似 <c>git -C</c>）。相对路径按启动时的当前工作目录解析。
/// </summary>
public static class CliWorkingDirectory
{
    /// <summary>
    /// 从命令行参数中剥离 <c>--cwd</c>/<c>-C</c> 及其值。支持三种形式：
    /// <c>--cwd &lt;path&gt;</c>、<c>--cwd=&lt;path&gt;</c>、<c>-C &lt;path&gt;</c>；多次出现时最后一个生效。
    /// </summary>
    /// <param name="Path">提取到的目录路径；未指定时为 null。</param>
    /// <param name="Error">用法错误提示；非 null 时调用方应打印并以用法错误退出码终止。</param>
    /// <param name="Remaining">剥离该选项后的参数，保持原有顺序。</param>
    public sealed record ParseResult(string? Path, string? Error, string[] Remaining);

    public static ParseResult Parse(string[] args)
    {
        string? path = null;
        var remaining = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--cwd" or "-C")
            {
                if (i + 1 >= args.Length)
                    return new ParseResult(null, $"用法错误：{arg} 后缺少目录参数（{arg} <path>）", []);
                path = args[++i];
            }
            else if (arg.StartsWith("--cwd=", StringComparison.Ordinal))
            {
                path = arg["--cwd=".Length..];
                if (path.Length == 0)
                    return new ParseResult(null, "用法错误：--cwd= 后缺少目录路径", []);
            }
            else
            {
                remaining.Add(arg);
            }
        }

        return new ParseResult(path, null, [.. remaining]);
    }

    /// <summary>
    /// 将进程工作目录切换到 <paramref name="path"/>：解析为绝对路径并校验目录存在，
    /// 避免后续所有依赖 <see cref="Environment.CurrentDirectory"/> 的服务落在错误位置。
    /// </summary>
    /// <param name="path">目标目录，相对路径按当前工作目录解析。</param>
    /// <param name="error">失败原因（目录不存在 / 路径非法），可直接输出给用户。</param>
    /// <returns>切换成功返回 true。</returns>
    public static bool TryApply(string path, out string? error)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full))
            {
                error = $"--cwd 目录不存在：{full}";
                return false;
            }

            Directory.SetCurrentDirectory(full);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"--cwd 路径无效：{path}（{ex.Message}）";
            return false;
        }
    }
}
