using System.Text.Json.Serialization;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 子系统 JSON 序列化 Source Generator（H-15）。
/// 消除运行时反射，支持 AOT 发布和提升高频序列化性能。
/// </summary>
[JsonSerializable(typeof(HookPayload))]
[JsonSerializable(typeof(HookResult))]
[JsonSerializable(typeof(HookConfig))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true)]
internal partial class HookSerializerContext : JsonSerializerContext;
