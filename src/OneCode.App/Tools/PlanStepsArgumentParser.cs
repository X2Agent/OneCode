using System.Text.Json.Nodes;

namespace OneCode.App.Tools;

/// <summary>
/// 解析模型（或供应商）通过 SubmitPlan 的 <c>steps</c> 参数产出的步骤数组。
///
/// <para>
/// 分层契约：<see cref="OneCode.Infrastructure.Middleware.ToolArgumentBindingMiddleware"/>（通用参数绑定容错）先行把顶层畸形形状
/// 归一化——二次编码的 JSON 字符串解一层、单对象包装成单元素数组；本解析器只负责 SubmitPlan
/// 领域相关的形状：camelCase / PascalCase / snake_case 别名键、<c>{"steps":[...]}</c> 包装、
/// 字符串步骤元素、单字符串转单元素数组，以及"形似单步的对象不被包装分支吞掉"的守卫。
/// </para>
/// <para>
/// 解析遵循"单调放宽"：标准数组形状原样解析，常见的畸形包装形状兼容解析，
/// 无法解析的形状返回可自纠正的失败原因（记录到日志，模型收到普通工具错误而非 run 终止）。
/// <see cref="Parse"/> 本身<b>永不抛异常</b>。
/// </para>
/// </summary>
internal static class PlanStepsArgumentParser
{
    // 键名大小写不敏感（对齐 AIJsonUtilities.DefaultOptions 的 PropertyNameCaseInsensitive=true）。
    // 别名只收录有仓库内文本依据的一组：CreatePlanTool 的 steps 参数描述向模型展示的是
    // "title/label"、"description/content"、"dependencies"，
    // 而 schema 字段名是 camelCase——按描述产出别名的模型需要在这里被接住。
    // depends_on 同源：ParallelAgents 的参数描述用的是 snake_case 写法。
    // 无依据的猜测键一律不收录（未知键按忽略处理，交由失败路径引导模型按 schema 重发）。
    private static readonly string[] IdKeys = ["id"];
    private static readonly string[] TitleKeys = ["title", "label"];
    private static readonly string[] DescriptionKeys = ["description", "content"];
    private static readonly string[] FilesKeys = ["files"];
    private static readonly string[] AcceptanceCriteriaKeys = ["acceptanceCriteria"];
    private static readonly string[] DependsOnKeys = ["dependsOn", "dependencies", "depends_on"];
    private static readonly string[] RiskKeys = ["risk"];
    private static readonly string[] AssigneeKeys = ["assignee"];
    private static readonly string[] StatusKeys = ["status"];

    /// <summary>包装对象键：只收纳参数自身的名字——模型偶尔把数组再包一层（如 <c>{"steps":[...]}</c>）。</summary>
    private static readonly string[] WrapperKeys = ["steps"];

    /// <summary>
    /// 解析 <c>steps</c> 参数的原始值；<b>永不抛异常</b>。
    /// 原始值通常是被 ToolArgumentBindingMiddleware 归一化后的 <see cref="JsonElement"/>，
    /// 其余类型仅为程序化调用方保留。
    /// </summary>
    public static PlanStepsParseResult Parse(object? rawValue)
    {
        switch (rawValue)
        {
            case null:
                // 未提供 steps：不是畸形输入，不需要日志告警。
                return new PlanStepsParseResult([], FailureReason: null);
            case PlanStepDto[] alreadyTyped:
                return alreadyTyped.Length > 0
                    ? new PlanStepsParseResult(alreadyTyped, FailureReason: null)
                    : Failure("steps array was empty");
            case JsonElement element:
                return ParseElement(element);
            case JsonDocument document:
                return ParseElement(document.RootElement);
            case JsonNode node:
                return node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var nodeText)
                    ? ParseText(nodeText)
                    : ParseElement(JsonSerializer.SerializeToElement(node));
            case string text:
                return ParseText(text);
            default:
                return Failure(
                    $"steps had an unsupported .NET type '{rawValue.GetType().Name}' (expected a JSON array of step objects)");
        }
    }

    private static PlanStepsParseResult ParseElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => ParseArray(element),
        JsonValueKind.Object => ParseObject(element),
        JsonValueKind.String => ParseText(element.GetString() ?? string.Empty),
        // JSON null 视作未提供步骤，不作为格式错误。
        JsonValueKind.Null or JsonValueKind.Undefined => new PlanStepsParseResult([], FailureReason: null),
        _ => Failure($"steps was JSON {element.ValueKind} (expected a JSON array of step objects)"),
    };

    private static PlanStepsParseResult ParseArray(JsonElement array)
    {
        List<PlanStepDto> steps = [];
        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties = EnumerateProperties(item);
                    if (!LooksLikeStep(properties) && TryUnwrapSteps(properties, out var wrapped))
                    {
                        // 通用绑定层把顶层包装对象（{"steps":[…]}）包成单元素数组后的产物。
                        steps.AddRange(ParseArray(wrapped).Steps ?? []);
                    }
                    else
                    {
                        steps.Add(ParseStep(properties));
                    }

                    break;
                case JsonValueKind.String:
                    steps.Add(TextStep(item.GetString() ?? string.Empty, steps.Count + 1));
                    break;
                default:
                    // null / 数字 / 布尔元素没有步骤语义，跳过（是否可用交由后续校验判定）。
                    break;
            }
        }

        return steps.Count > 0
            ? new PlanStepsParseResult(steps.ToArray(), FailureReason: null)
            : Failure("steps array contained no usable step objects");
    }

    private static PlanStepsParseResult ParseObject(JsonElement element)
    {
        var properties = EnumerateProperties(element);

        // 先判"形似单步"：否则 {"title":…,"steps":[…]}/含同名数组字段的合法步骤会被包装分支吞掉。
        if (LooksLikeStep(properties))
            return new PlanStepsParseResult([ParseStep(properties)], FailureReason: null);

        if (TryUnwrapSteps(properties, out var wrapped))
            return ParseArray(wrapped);

        return Failure("steps was a JSON object without step fields or a nested steps array");
    }

    /// <summary>纯文本步骤（顶层字符串或字符串元素）：自动编号，标题与描述同文本；验收标准等缺失由后续校验器报出。</summary>
    private static PlanStepsParseResult ParseText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0
            ? new PlanStepsParseResult([TextStep(trimmed, 1)], FailureReason: null)
            : Failure("steps was an empty string");
    }

    private static PlanStepDto ParseStep(IReadOnlyDictionary<string, JsonElement> properties)
    {
        return new PlanStepDto
        {
            Id = GetString(properties, IdKeys),
            Title = GetString(properties, TitleKeys),
            Description = GetString(properties, DescriptionKeys),
            Files = GetStringArray(properties, FilesKeys),
            AcceptanceCriteria = GetStringArray(properties, AcceptanceCriteriaKeys),
            DependsOn = GetStringArray(properties, DependsOnKeys),
            Risk = GetString(properties, RiskKeys),
            Assignee = GetString(properties, AssigneeKeys),
            Status = GetString(properties, StatusKeys),
        };
    }

    private static PlanStepDto TextStep(string text, int index) => new()
    {
        Id = $"step-{index}",
        Title = text,
        Description = text,
    };

    /// <summary>把对象属性摊成大小写不敏感的字典；仅大小写不同的重复键保持"后者胜出"的宽松语义。</summary>
    private static Dictionary<string, JsonElement> EnumerateProperties(JsonElement element)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            properties[property.Name] = property.Value;
        return properties;
    }

    private static bool LooksLikeStep(IReadOnlyDictionary<string, JsonElement> properties)
        => IdKeys.Any(properties.ContainsKey)
           || TitleKeys.Any(properties.ContainsKey)
           || DescriptionKeys.Any(properties.ContainsKey);

    private static bool TryUnwrapSteps(IReadOnlyDictionary<string, JsonElement> properties, out JsonElement wrapped)
    {
        foreach (var wrapperKey in WrapperKeys)
        {
            if (properties.TryGetValue(wrapperKey, out wrapped) && wrapped.ValueKind == JsonValueKind.Array)
                return true;
        }

        wrapped = default;
        return false;
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> properties, string[] keys)
    {
        foreach (var key in keys)
        {
            if (properties.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static string[]? GetStringArray(IReadOnlyDictionary<string, JsonElement> properties, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!properties.TryGetValue(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Array)
            {
                var items = value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .OfType<string>()
                    .ToArray();
                return items.Length > 0 ? items : null;
            }

            // 单字符串取一项（模型常见的 { "files": "src/A.cs" } 形状）。
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } single)
                return [single];

            return null;
        }

        return null;
    }

    private static PlanStepsParseResult Failure(string reason)
        => new(Steps: null, FailureReason: reason);
}

/// <summary>
/// <see cref="PlanStepsArgumentParser"/> 的解析结果：<see cref="Steps"/> 非空表示可提交；
/// <see cref="FailureReason"/> 非空表示形状不可用（用于日志记录与测试断言）。
/// </summary>
internal sealed record PlanStepsParseResult(PlanStepDto[]? Steps, string? FailureReason);