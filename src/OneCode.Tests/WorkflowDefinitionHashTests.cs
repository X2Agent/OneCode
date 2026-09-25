using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneCode.Core.Workflows;

namespace OneCode.Tests;

/// <summary>
/// 工作流定义哈希内核的输出契约测试：钉住「与直接 SHA256 表达式逐字等价」，
/// 拦截大写 / 截断 / 缩进 / encoder 之类的全局漂移（这类漂移会让既有持久化记录集体判失配）。
/// </summary>
public sealed class WorkflowDefinitionHashTests
{
    [Fact]
    public void Compute_MatchesRawSha256LowerHex()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"schema":"test-v1"}""");

        WorkflowDefinitionHash.Compute(bytes)
            .Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void ComputeText_MatchesUtf8Sha256()
    {
        const string canonical = """{"value":"中文<&> emoji 🎯"}""";

        WorkflowDefinitionHash.ComputeText(canonical)
            .Should().Be(Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant());
    }

    [Fact]
    public void ComputeObject_MatchesSerializerThenSha256()
    {
        var payload = new { schema = "test-v1", text = "中文<&>\"" };

        WorkflowDefinitionHash.ComputeObject(payload)
            .Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(payload)))).ToLowerInvariant());
    }

    [Fact]
    public void ComputeCanonical_MatchesWriterThenSha256()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "test-v1");
            writer.WriteNumber("mode", 2);
            writer.WriteEndObject();
        }

        var expected = Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();

        WorkflowDefinitionHash.ComputeCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "test-v1");
            writer.WriteNumber("mode", 2);
            writer.WriteEndObject();
        }).Should().Be(expected);
    }

    [Fact]
    public void DescribeSerializerOptions_NullMapsToDefaultLiteral()
    {
        WorkflowDefinitionHash.DescribeSerializerOptions(null).Should().Be("default");
        WorkflowDefinitionHash.DescribeSerializerOptions(new JsonSerializerOptions())
            .Should().NotBe("default", "显式 options 与 null 必须产出不同的签名成分");
    }

    [Fact]
    public void OutputContract_IsLowercase64Hex()
    {
        WorkflowDefinitionHash.ComputeText("x")
            .Should().HaveLength(64)
            .And.MatchRegex("^[0-9a-f]{64}$");
    }
}
