using OneCode.Infrastructure;

namespace OneCode.App.Tools;

/// <summary>
/// 文件/目录工具共用的路径安全守卫：SafeResolve（工作目录 + /add-dir 附加目录边界）
/// + 失败转标准错误 ToolResult。所有接受用户路径输入的工具必须经此解析，禁止直接拼路径。
/// </summary>
internal static class ToolPathGuard
{
    /// <summary>
    /// 解析工具输入路径（相对或绝对，支持 ~ 展开）为安全绝对路径。
    /// 成功时返回 null 并输出 <paramref name="fullPath"/>；失败时返回错误 ToolResult
    /// （消息保留 SafeResolve 的越界/格式说明，供 LLM 自行纠正）。
    /// </summary>
    public static ToolResult? Resolve(string rawPath, IWorkingDirectoryAccessor wd, out string fullPath)
    {
        var result = PathsHelper.SafeResolve(rawPath, wd.WorkingDirectory, wd.AdditionalDirectories);
        if (result.IsSuccess)
        {
            fullPath = result.Value!;
            return null;
        }

        fullPath = string.Empty;
        return ToolResult.Error($"Error: {result.Error}");
    }
}
