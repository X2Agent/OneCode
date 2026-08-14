namespace OneCode.Core.Tools;

/// <summary>
/// 统一的工具参数提取器。
///
/// 存在意义：文件编辑工具的路径参数在运行时存在多种命名约定和多种入参类型，
/// 历史上各中间件/权限层各自实现 <c>ExtractPath</c>，导致：
/// 1. 命名不一致——工具参数名是 <c>filePath</c>，
///    而提取方查 <c>path</c>/<c>file_path</c>，名字对不上，路径永远提取不到。
/// 2. 类型不一致——运行时中间件收到的 <c>ctx.Arguments</c> 实际是
///    <c>AIFunctionArguments</c>（继承自 <c>Dictionary&lt;string, object?&gt;</c>），
///    并非 <see cref="JsonElement"/>，但部分中间件仅判 <c>arguments is JsonElement</c>，
///    导致提取逻辑根本进不去。
///
/// 本类作为单一事实源，统一处理上述差异。所有需要从工具参数中提取路径/命令/URL
/// 的中间件和权限组件均应引用此类，不得再各自实现。
/// </summary>
public static class ToolArgumentExtractor
{
    /// <summary>
    /// 文件路径参数的候选 key，按优先级排列。
    /// filePath 是 OneCode 工具的规范参数名，file_path 是 MCP 工具的 snake_case 约定。
    /// </summary>
    private static readonly string[] FilePathKeys =
    [
        "filePath",
        "file_path",
        "path",
    ];

    /// <summary>
    /// 将工具调用参数（AIFunctionArguments / IDictionary&lt;string, object?&gt;）
    /// 转换为参数字典，供安全不变量、行为契约等中间件统一使用。
    ///
    /// 参数解析失败时 fail-closed 返回 null，调用方必须阻止工具执行。
    ///
    /// 转换规则：
    /// <list type="bullet">
    ///   <item>arguments 为 null → 返回空字典（非 null）</item>
    ///   <item>JsonElement.String → 解包为 string</item>
    ///   <item>JsonElement（非 String）→ 保持原样</item>
    ///   <item>其他类型 → 保持原样</item>
    ///   <item>异常 → 记录日志并返回 null（fail-closed）</item>
    /// </list>
    /// </summary>
    /// <param name="arguments">工具调用参数，通常为 <c>ctx.Arguments</c>（AIFunctionArguments）。</param>
    /// <param name="toolName">工具名称，仅用于日志。为 null 时记录 "(unknown)"。</param>
    /// <param name="logger">日志器，为 null 时跳过错误日志。</param>
    /// <returns>参数字典；解析失败时返回 null（fail-closed 信号）。</returns>
    public static Dictionary<string, object?>? ToParameterDictionary(
        IDictionary<string, object?>? arguments,
        string? toolName = null,
        ILogger? logger = null)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (arguments is null)
            return dict;

        try
        {
            foreach (var kvp in arguments)
            {
                dict[kvp.Key] = kvp.Value switch
                {
                    JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                    JsonElement el => el,
                    _ => kvp.Value,
                };
            }
        }
        catch (Exception ex)
        {
            // 参数解析失败时 fail-closed — 返回 null 阻止工具执行
            logger?.LogError(ex, "Failed to extract parameters for tool {ToolName}", toolName ?? "(unknown)");
            return null;
        }

        return dict;
    }

    /// <summary>
    /// 正确处理多种入参类型：
    /// - <c>AIFunctionArguments</c> / <c>Dictionary&lt;string, object?&gt;</c>
    ///   （运行时中间件收到的 <c>ctx.Arguments</c> 即此类型）
    /// - <see cref="JsonElement"/>（序列化后或测试构造的场景）
    /// - 任意可序列化对象（fallback 序列化为 JSON 后查询）
    /// </summary>
    /// <param name="arguments">工具调用参数，可能为 <c>AIFunctionArguments</c>、<c>Dictionary</c>、<c>JsonElement</c> 等。</param>
    /// <returns>提取到的文件路径字符串；无法提取时返回 null。</returns>
    public static string? ExtractFilePath(object? arguments)
    {
        if (arguments is null) return null;

        // 1. 字典路径：运行时主路径。AIFunctionArguments 继承自 Dictionary<string, object?>，
        //    值可能是 string、JsonElement 或其它类型。
        if (arguments is IDictionary<string, object?> dict)
        {
            foreach (var key in FilePathKeys)
            {
                if (!dict.TryGetValue(key, out var value) || value is null)
                    continue;

                if (value is string s)
                    return s;

                if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
                    return je.GetString();
            }

            return null;
        }

        // 2. JsonElement 路径：测试或序列化后场景
        if (arguments is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            return ExtractFilePathFromElement(el);
        }

        // 3. Fallback：序列化后按 JSON 查询
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
            return ExtractFilePathFromElement(doc.RootElement);
        }
        catch (Exception ex)
        {
            // 静态方法无 ILogger 注入，按 AGENTS.md §5.1 兜底使用 Debug.WriteLine。
            // 此处失败会导致权限校验退化为工具名匹配，必须有可观测信号。
            System.Diagnostics.Debug.WriteLine(
                $"ToolArgumentExtractor.ExtractFilePath fallback serialization failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 <see cref="JsonElement"/> 参数对象中提取文件路径。
    /// 供权限层（入参已是 JsonElement）直接使用。
    /// </summary>
    public static string? ExtractFilePath(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return null;

        return ExtractFilePathFromElement(input);
    }

    /// <summary>
    /// 提取工具调用的"输入字符串"，用于权限规则的 InputPattern glob 匹配。
    ///
    /// 语义：
    /// - <c>Bash</c> / <c>PowerShell</c> → <c>command</c> 字段
    /// - <c>WebFetch</c> → <c>url</c> 字段
    /// - 文件编辑/读取工具 → 文件路径
    /// - 其他 → null（调用方自行决定 fallback 行为）
    /// </summary>
    /// <param name="toolName">工具名称（不区分大小写）。</param>
    /// <param name="input">工具输入参数（JSON 序列化后的 JsonElement）。</param>
    /// <returns>用于规则匹配的输入字符串；无匹配时返回 null。</returns>
    public static string? ExtractInputString(string toolName, JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return input.GetString();

        if (input.ValueKind != JsonValueKind.Object)
            return null;

        // Shell 工具优先查 command
        if (IsShellTool(toolName))
        {
            if (input.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                return cmd.GetString();
        }

        // WebFetch 查 url
        if (IsToolName(toolName, "WebFetch"))
        {
            if (input.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return url.GetString();
        }

        // 文件工具查路径（统一走 ExtractFilePath，覆盖所有路径 key 约定）
        return ExtractFilePathFromElement(input);
    }

    private static string? ExtractFilePathFromElement(JsonElement el)
    {
        foreach (var key in FilePathKeys)
        {
            if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }

        return null;
    }

    private static bool IsShellTool(string toolName) =>
        IsToolName(toolName, "Bash") || IsToolName(toolName, "PowerShell");

    private static bool IsToolName(string toolName, string expected) =>
        string.Equals(toolName, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 构建工具调用的人类可读操作摘要，用于权限提示 UI。
    ///
    /// 遍历一组常见的“描述性”字段（command/path/file_path/url/query/description），
    /// 返回第一个非空字符串值（超长截断）。无匹配时返回形如
    /// <c>"Execute {toolName}"</c> 的通用描述。
    ///
    /// 与 <see cref="ExtractInputString"/> 的区别：后者按工具类型优先级提取，
    /// 语义更窄且可能返回 null；本方法面向权限提示展示，覆盖更广的字段集合并保证非空返回。
    /// </summary>
    /// <param name="toolName">工具名称，用于生成 fallback 描述。</param>
    /// <param name="input">工具输入参数（JSON 序列化后的 JsonElement）。</param>
    /// <param name="maxLength">摘要最大长度，超出部分以省略号截断。默认 160。</param>
    /// <returns>非空的操作摘要字符串。</returns>
    public static string BuildToolDescription(string toolName, JsonElement input, int maxLength = 160)
    {
        foreach (var field in s_descriptionFields)
        {
            if (input.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var text = val.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Length > maxLength ? text[..maxLength] + "\u2026" : text;
            }
        }
        return $"Execute {toolName}";
    }

    private static readonly string[] s_descriptionFields =
    [
        "command", "filePath", "file_path", "url", "query", "description"
    ];
}
