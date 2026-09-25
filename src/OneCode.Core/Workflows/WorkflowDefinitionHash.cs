using System.Security.Cryptography;
using System.Text;

namespace OneCode.Core.Workflows;

/// <summary>
/// 工作流定义哈希的统一内核：五处定义指纹（Build 受控 attempt / Goal / Team 任务 / Team 审批 /
/// 子代理任务）共用同一份输出契约。
///
/// <para>冻结契约（改动即导致已持久化记录的漂移检测失配）：canonical 字节 → SHA256 →
/// 64 字符小写 hex；对象路径走默认 options 的 <c>JsonSerializer.Serialize</c>，
/// writer 路径用默认 <see cref="JsonWriterOptions"/>。</para>
///
/// <para>各工作流的 canonical schema（字段集、顺序、comparer、是否含 <c>serializerOptions</c>
/// 签名成分）逐处保留——内核不提供必填字段模板，统一字段集会改变哈希并拒绝已有 run 的恢复。</para>
/// </summary>
public static class WorkflowDefinitionHash
{
    /// <summary>canonical UTF-8 字节 → 小写 hex SHA256。</summary>
    public static string Compute(ReadOnlySpan<byte> canonicalUtf8)
        => Convert.ToHexString(SHA256.HashData(canonicalUtf8)).ToLowerInvariant();

    /// <summary>canonical 文本（UTF-8 编码后）→ 小写 hex SHA256。</summary>
    public static string ComputeText(string canonical)
        => Compute(Encoding.UTF8.GetBytes(canonical));

    /// <summary>
    /// 与 <c>JsonSerializer.Serialize&lt;TValue&gt;(value)</c>（默认 options）逐字等价的对象哈希。
    /// </summary>
    public static string ComputeObject<TValue>(TValue payload)
        => ComputeText(JsonSerializer.Serialize(payload));

    /// <summary>
    /// 用 <see cref="Utf8JsonWriter"/>（默认选项）手写 canonical payload 并哈希。
    /// </summary>
    public static string ComputeCanonical(Action<Utf8JsonWriter> writePayload)
    {
        ArgumentNullException.ThrowIfNull(writePayload);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writePayload(writer);
        }

        return Compute(stream.ToArray());
    }

    /// <summary>
    /// 序列化契约描述符：null → <c>"default"</c>。作为签名成分写入 canonical payload 时，
    /// 必须使用本方法（而不是自定义 options 摘要），以保证历史哈希不变。
    /// </summary>
    public static string DescribeSerializerOptions(JsonSerializerOptions? options)
        => options is null ? "default" : JsonSerializer.Serialize(options);
}
