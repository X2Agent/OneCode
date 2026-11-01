using Microsoft.Extensions.AI;

namespace OneCode.Core.Tools;

/// <summary>
/// 工具目标路径的唯一解析实现：从 <see cref="AIFunction"/> 的 <c>JsonSchema</c> 推断「哪些参数是目标路径」，
/// 而不是按固定参数名白名单扫描调用参数。
///
/// 解析分两级：
/// <list type="number">
///   <item><description>Tier A——规范键 <c>filePath</c> / <c>file_path</c> / <c>path</c> / <c>file</c>
///   （大小写不敏感）：schema 里出现的全部纳入，<see cref="ToolTargetPathResult.Paths"/> 按此优先级排列；</description></item>
///   <item><description>Tier B——其余以 <c>path</c> 结尾的参数名（<c>targetPath</c>、<c>sourcePath</c>、
///   <c>dirPath</c> 等）全部纳入，按 schema 声明顺序排在规范键之后。安全校验必须覆盖所有路径形参数：
///   只挑一个，模型就能把真实目标挪到未校验的参数上绕过工作区边界。</description></item>
/// </list>
/// 刻意不把 <c>target</c> / <c>directory</c> 这类通用名词列为规范键：它们在不碰文件系统的工具上
/// 会把「无路径概念」误判成「路径不可用」，从而误拒正常调用。
///
/// 语义边界（回归时务必保留）：
/// <list type="bullet">
///   <item><description>schema 里没有路径参数 → <see cref="ToolTargetPathKind.None"/>（工具不触达文件系统）；</description></item>
///   <item><description>路径参数可选且本次未提供 → <see cref="ToolTargetPathKind.None"/>，不替工具假设「默认就是 cwd」；</description></item>
///   <item><description>任一路径参数必填但缺失 / 非字符串 / 空串，或声明为非字符串类型 →
///   <see cref="ToolTargetPathKind.Unresolvable"/>，调用必须被拒绝（不让另一个可用的键掩盖它）；</description></item>
///   <item><description>schema 同时声明多个规范键（如 <c>Lsp</c> 的 <c>file</c> 与 <c>path</c>）→ 本次提供了的键全部进入
///   <see cref="ToolTargetPathResult.Paths"/>，由安全层逐一校验；只取其中一个，
///   「只传另一个键」的调用就会整体失去路径检查。</description></item>
/// </list>
/// </summary>
public static class ToolTargetPathResolver
{
    /// <summary>Tier A：规范路径参数名，按优先级排列。</summary>
    private static readonly string[] CanonicalPathKeys = ["filePath", "file_path", "path", "file"];

    /// <summary>Tier B：以该后缀结尾的参数名视为路径（<c>path</c> / <c>Path</c>）。</summary>
    private const string PathSuffix = "path";

    private enum ArgumentState
    {
        Missing,
        String,
        NonString,
    }

    /// <summary>解析一次工具调用的目标路径。</summary>
    /// <param name="function">被调用的工具；它的 <c>JsonSchema</c> 是唯一推断依据。</param>
    /// <param name="arguments">本次调用参数（<c>ToolArgumentExtractor.ToParameterDictionary</c> 的产物）。</param>
    public static ToolTargetPathResult Resolve(
        AIFunction function,
        IReadOnlyDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(function);

        List<string> paths = [];
        foreach (var (parameterName, propertySchema) in FindPathParameters(function.JsonSchema))
        {
            var single = ResolveParameter(function, arguments, parameterName, propertySchema);
            if (single.Kind == ToolTargetPathKind.Unresolvable)
                return single;

            paths.AddRange(single.Paths);
        }

        return paths.Count > 0 ? ToolTargetPathResult.Resolved(paths) : ToolTargetPathResult.None;
    }

    /// <summary>单个路径参数的三态解析：None（可选未提供）/ Resolved（一条路径）/ Unresolvable。</summary>
    private static ToolTargetPathResult ResolveParameter(
        AIFunction function,
        IReadOnlyDictionary<string, object?>? arguments,
        string parameterName,
        JsonElement propertySchema)
    {
        var declaredTypes = ReadTypes(propertySchema);
        if (declaredTypes.Count > 0
            && !declaredTypes.Contains("string", StringComparer.OrdinalIgnoreCase))
        {
            return ToolTargetPathResult.Unresolvable(
                $"Tool '{function.Name}' declares path parameter '{parameterName}' as " +
                $"'{string.Join("|", declaredTypes)}', which cannot be verified against the workspace boundary. " +
                "Pass a plain string path.");
        }

        var required = IsRequired(function.JsonSchema, parameterName);
        switch (ReadArgument(arguments, parameterName, out var value))
        {
            case ArgumentState.Missing:
                // 可选路径缺省：留给工具自身的默认目标，安全层不施加路径约束。
                return required
                    ? ToolTargetPathResult.Unresolvable(
                        $"Tool '{function.Name}' requires path parameter '{parameterName}' but this call did not provide it. " +
                        "Retry with an explicit path.")
                    : ToolTargetPathResult.None;

            case ArgumentState.NonString:
                return ToolTargetPathResult.Unresolvable(
                    $"Tool '{function.Name}' parameter '{parameterName}' must be a string path, " +
                    "but this call passed a non-string value.");

            default:
                if (string.IsNullOrWhiteSpace(value))
                {
                    return required
                        ? ToolTargetPathResult.Unresolvable(
                            $"Tool '{function.Name}' parameter '{parameterName}' is required but was empty.")
                        : ToolTargetPathResult.None;
                }

                return ToolTargetPathResult.Resolved([value]);
        }
    }

    /// <summary>解析并只取主目标路径（单路径消费者用，如编辑事务快照、Edit 存在性检查）。</summary>
    public static string? ResolvePrimaryPath(
        AIFunction function,
        IReadOnlyDictionary<string, object?>? arguments)
        => Resolve(function, arguments).PrimaryPath;

    /// <summary>
    /// 找出 schema 声明的全部目标路径参数：Tier A 规范键按 <see cref="CanonicalPathKeys"/> 优先级排列在前，
    /// 其余以 <c>path</c> 结尾的参数全部追加在后（schema 声明顺序）。
    /// 安全校验必须覆盖所有路径形参数——只挑一个，模型就能把真实目标挪到未校验的参数上绕过边界。
    /// </summary>
    private static List<(string Name, JsonElement Schema)> FindPathParameters(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<(string Name, JsonElement Schema)> result = [];

        // Tier A：按优先级收集规范键。与参数取值（ReadArgument）的 OrdinalIgnoreCase 口径一致，
        // 此处同样忽略大小写——Path/FILEPATH 形态不能依赖 Tier B 兜底。
        foreach (var key in CanonicalPathKeys)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                    result.Add((property.Name, property.Value));
            }
        }

        // Tier B：其余以 path 结尾的参数全部纳入（不再只取第一个）。
        foreach (var property in properties.EnumerateObject())
        {
            if (IsCanonicalKey(property.Name))
                continue;

            if (property.Name.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
                result.Add((property.Name, property.Value));
        }

        return result;
    }

    /// <summary>参数名是否命中 Tier A 规范键（大小写不敏感）；Tier B 收集时用于跳过已纳入的规范键。</summary>
    private static bool IsCanonicalKey(string name) =>
        CanonicalPathKeys.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>读取 schema 声明的类型集合；空集合表示未声明（按字符串处理）。</summary>
    private static List<string> ReadTypes(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object)
            return [];

        if (propertySchema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
                return [type.GetString()!];

            if (type.ValueKind == JsonValueKind.Array)
            {
                return [.. type.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)];
            }
        }

        // anyOf/oneOf 联合类型：任一分支声明 string 即视为字符串参数。
        foreach (var union in new[] { "anyOf", "oneOf" })
        {
            if (!propertySchema.TryGetProperty(union, out var branches) || branches.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var branch in branches.EnumerateArray())
            {
                if (branch.ValueKind == JsonValueKind.Object
                    && branch.TryGetProperty("type", out var branchType)
                    && branchType.ValueKind == JsonValueKind.String
                    && string.Equals(branchType.GetString(), "string", StringComparison.OrdinalIgnoreCase))
                {
                    return ["string"];
                }
            }
        }

        return [];
    }

    private static bool IsRequired(JsonElement schema, string parameterName)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ArgumentState ReadArgument(
        IReadOnlyDictionary<string, object?>? arguments,
        string parameterName,
        out string value)
    {
        value = string.Empty;
        if (!TryFindRawArgument(arguments, parameterName, out var raw))
            return ArgumentState.Missing;

        switch (raw)
        {
            case null:
                return ArgumentState.Missing;

            case string text:
                value = text;
                return ArgumentState.String;

            case JsonElement element when element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined:
                return ArgumentState.Missing;

            case JsonElement element when element.ValueKind == JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return ArgumentState.String;

            default:
                return ArgumentState.NonString;
        }
    }

    /// <summary>按大小写不敏感方式取参数值；调用方字典通常是 OrdinalIgnoreCase，此处再做一次兜底。</summary>
    private static bool TryFindRawArgument(
        IReadOnlyDictionary<string, object?>? arguments,
        string parameterName,
        out object? raw)
    {
        raw = null;
        if (arguments is null)
            return false;

        if (arguments.TryGetValue(parameterName, out raw))
            return true;

        foreach (var pair in arguments)
        {
            if (!string.Equals(pair.Key, parameterName, StringComparison.OrdinalIgnoreCase))
                continue;

            raw = pair.Value;
            return true;
        }

        return false;
    }
}
