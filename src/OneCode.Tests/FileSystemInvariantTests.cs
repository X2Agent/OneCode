using OneCode.Infrastructure.Middleware.Invariants;

namespace OneCode.Tests;

/// <summary>
/// <see cref="FileSystemInvariant"/> 敏感路径规则的行为锁：
/// 读取类工具（含检索类 Grep/Glob/LS）直连敏感路径必须 Layer 0 拒绝；
/// 检索工具此前不在 ReadTools 内，其 path 参数完全绕过敏感读保护。
/// </summary>
public sealed class FileSystemInvariantTests
{
    private const string WorkingDir = @"C:\work\repo";

    private static async Task<InvariantCheckResult> CheckAsync(
        string toolName, string path, string pathKey = "path")
    {
        var invariant = new FileSystemInvariant(WorkingDir);
        var parameters = new Dictionary<string, object?> { [pathKey] = path };
        return await invariant.CheckAsync(toolName, parameters, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Cat")]
    public async Task ReadTool_SensitiveFile_Denied(string tool)
    {
        var result = await CheckAsync(tool, ".env", "filePath");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain(".env");
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("LS")]
    public async Task SearchTool_DirectSensitiveTarget_Denied(string tool)
    {
        // 反证锚点：Grep/Glob/LS 不在 ReadTools 时，此处会静默 Allow——
        // .env 内容即可经检索工具进入对话上下文。
        var result = await CheckAsync(tool, "config/.env");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain(".env");
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("LS")]
    public async Task SearchTool_NormalPath_Allowed(string tool)
    {
        var result = await CheckAsync(tool, "src");

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task SearchTool_SshKeySequence_Denied()
    {
        var result = await CheckAsync("Grep", "deploy/keys/id_rsa");

        result.Allowed.Should().BeFalse();
    }
}
