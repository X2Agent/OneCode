using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// 工具参数绑定容错：在 AIFunction 反射编组（MEAI <c>ReflectionAIFunctionDescriptor</c>）之前，
/// 按工具 schema 归一化模型产出的畸形数组参数，并把编组期 <see cref="JsonException"/>
/// 转为可自纠正的错误结果（<c>ToolResult.Error</c>）。
///
/// <para>
/// 问题背景：参数编组发生在工具方法体之前，<c>string[]</c> / DTO 数组参数收到字符串或单对象时
/// 在反射编组阶段直接抛 <see cref="JsonException"/>——工具内部的 try/catch 无法兜底；
/// FunctionInvokingChatClient 只向模型回报 "Error: Function failed."（无细节），模型只能原样重试；
/// 连续失败达到 <c>MaximumConsecutiveErrorsPerRequest</c> 后异常被原样抛出，整轮 run 终止。
/// </para>
///
/// <para>
/// 归一化只针对 schema 中 <c>type</c> 含 <c>array</c> 的顶层参数（子对象内的数组字段交给
/// 编组错误兜底路径），且只处理 JSON 来源的 <see cref="JsonElement"/> 值；程序化调用方传入的
/// .NET 原生类型不在此改写。该层只读取 <see cref="AIFunctionDeclaration.JsonSchema"/> 与
/// <see cref="FunctionInvocationContext.Arguments"/>，不重写发给模型的 schema。
/// </para>
/// </summary>
public sealed class ToolArgumentBindingMiddleware(ILogger? logger = null)
{
    private const string ArrayFixHint =
        "Array parameters must be sent as a JSON array (e.g. [\"a\",\"b\"]), not a string or a single object.";

    /// <summary>创建 MAF 中间件委托。须注册在 <see cref="ToolResultUnwrapMiddleware"/> 的内层。</summary>
    /// <remarks>
    /// 返回的错误结果（<c>ToolResult.Error</c>）由外层 Unwrap 序列化为字符串并写入
    /// <c>ToolExecutionContext.IsError</c>，模型因此能看到具体参数名与纠正提示，而不是
    /// "Error: Function failed."，也不会把编组失败累计进 FunctionInvokingChatClient 的连续错误上限。
    /// </remarks>
    public Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        CreateDelegate()
    {
        return async (_, ctx, next, ct) =>
        {
            if (ctx.Function is not null && TryNormalizeArrayArguments(ctx.Function, ctx.Arguments, out var failure))
            {
                logger?.LogWarning("Tool '{Tool}' received an unusable array argument: {Reason}",
                    ctx.Function.Name, failure.Content);
                return failure;
            }

            try
            {
                return await next(ctx, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // 归一化覆盖不到的形状（如 DTO 数组内的嵌套字段类型不匹配）：
                // 把编组异常转成带细节的工具错误，模型可按 Path 自纠正。
                logger?.LogWarning(ex, "Tool '{Tool}' argument marshalling failed", ctx.Function?.Name);
                return ToolResult.Error(
                    $"Tool argument binding failed: {ex.Message}. {ArrayFixHint}",
                    suggestedNextAction: "Resend the tool call with arguments matching the tool's JSON schema.");
            }
        };
    }

    /// <summary>
    /// 归一化 schema 声明为数组的参数。返回 <c>true</c> 表示存在无法归一化的参数
    /// （<paramref name="failure"/> 已携带错误结果，不应继续调用工具）。
    /// </summary>
    internal static bool TryNormalizeArrayArguments(AIFunction function, AIFunctionArguments arguments, out ToolResult failure)
    {
        failure = ToolResult.Error(string.Empty);
        var schema = function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // schema 属性名与参数名一致；模型可能产出大小写不同的键，因此按大小写不敏感匹配，
        // 回写时使用参数字典中的原始键，避免在 ordinal 字典里制造重复键。
        var schemaByProperty = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties.EnumerateObject())
            schemaByProperty[property.Name] = property.Value;

        foreach (var argument in arguments)
        {
            if (!schemaByProperty.TryGetValue(argument.Key, out var propertySchema) ||
                !IsArrayType(propertySchema) ||
                argument.Value is not JsonElement element)
            {
                continue;
            }

            var itemsKind = GetItemsType(propertySchema);
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                case JsonValueKind.Null:
                    break;

                case JsonValueKind.String:
                    {
                        var text = element.GetString() ?? string.Empty;
                        if (TryParseJsonArray(text, out var parsed))
                        {
                            // 数组被二次编码成 JSON 字符串（最常见畸形）：解一层后采纳。
                            arguments[argument.Key] = parsed;
                            break;
                        }

                        if (string.Equals(itemsKind, "object", StringComparison.Ordinal))
                        {
                            failure = ShapeError(argument.Key, itemsKind, "string");
                            return true;
                        }

                        // 无法解析的字符串：按单元素数组兜底（items 未声明或声明为 string）。
                        arguments[argument.Key] = WrapSingle(element);
                        break;
                    }

                case JsonValueKind.Object:
                    if (string.Equals(itemsKind, "string", StringComparison.Ordinal) ||
                        string.Equals(itemsKind, "number", StringComparison.Ordinal))
                    {
                        failure = ShapeError(argument.Key, itemsKind, "object");
                        return true;
                    }

                    // items 未声明或为 object：单对象包装成单元素数组。
                    arguments[argument.Key] = WrapSingle(element);
                    break;

                default:
                    failure = ShapeError(argument.Key, itemsKind, element.ValueKind.ToString());
                    return true;
            }
        }

        return false;
    }

    /// <summary>schema 属性的 <c>type</c> 含 <c>array</c>（type 可能是字符串或字符串数组，如 ["array","null"]）。</summary>
    private static bool IsArrayType(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object ||
            !propertySchema.TryGetProperty("type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => string.Equals(type.GetString(), "array", StringComparison.Ordinal),
            JsonValueKind.Array => type.EnumerateArray()
                .Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "array"),
            _ => false,
        };
    }

    /// <summary>读取 <c>items.type</c>；缺失或形状异常返回 null（视为未声明）。</summary>
    private static string? GetItemsType(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind == JsonValueKind.Object &&
            propertySchema.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Object &&
            items.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }

        return null;
    }

    private static bool TryParseJsonArray(string text, out JsonElement parsed)
    {
        parsed = default;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '[')
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            parsed = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement WrapSingle(JsonElement element)
        => JsonSerializer.SerializeToElement(new JsonArray(JsonNode.Parse(element.GetRawText())));

    private static ToolResult ShapeError(string parameterName, string? expectedItemsKind, string actualKind)
        => ToolResult.Error(
            $"Parameter '{parameterName}' must be a JSON array" +
            (expectedItemsKind is null ? "" : $" of {expectedItemsKind}") +
            $", but received JSON {actualKind}. {ArrayFixHint}",
            suggestedNextAction: "Resend the tool call with the argument as a proper JSON array.");
}