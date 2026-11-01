using OneCode.Core.Tools;
using OneCode.Infrastructure.Middleware.Invariants;

namespace OneCode.Tests;

/// <summary>
/// <see cref="FileSystemInvariant"/> 的行为锁：保护规则由 <see cref="ToolPathScope"/> 元数据分类决定，
/// 路径来自 <see cref="InvariantCheckRequest.TargetPath"/>（不再由本类猜参数名）。
/// 关键反证：Dynamic（MCP）工具读敏感文件必须拒绝，但读 <c>.git/config</c> 不得被写保护误拦；
/// provider 注入的会话状态工具与无路径工具必须完全放行。
/// </summary>
public sealed class FileSystemInvariantTests
{
    // 必须是当前平台的绝对路径：Path.GetFullPath(relative, basePath) 在 basePath 不是本平台
    // 根格式时抛 ArgumentException（Windows 风格 `C:\...` 在 Linux 上正属此种），
    // 会让所有相对路径用例走到「非法路径」的拒绝分支，测不到真正的路径判定。
    private static readonly string WorkingDir = Path.Combine(Path.GetTempPath(), "onecode-invariant-probe");

    private static Task<InvariantCheckResult> CheckAsync(string toolName, params string[] paths)
    {
        var invariant = new FileSystemInvariant(WorkingDir);
        var targetPath = paths.Length == 0
            ? ToolTargetPathResult.None
            : ToolTargetPathResult.Resolved(paths);

        return invariant.CheckAsync(
            new InvariantCheckRequest(toolName, new Dictionary<string, object?>(), targetPath),
            TestContext.Current.CancellationToken).AsTask();
    }

    [Fact]
    public async Task ReadTool_SensitiveFile_Denied()
    {
        var result = await CheckAsync("Read", ".env");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain(".env");
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("LS")]
    public async Task SearchTool_DirectSensitiveTarget_Denied(string tool)
    {
        // 反证锚点：检索工具一旦不在保护范围内，.env 内容即可经参数进入对话上下文。
        var result = await CheckAsync(tool, "config/.env");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain(".env");
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("LS")]
    public async Task SearchTool_NormalPath_Allowed(string tool)
        => (await CheckAsync(tool, "src")).Allowed.Should().BeTrue();

    [Fact]
    public async Task SearchTool_SshKeySequence_Denied()
        => (await CheckAsync("Grep", "deploy/keys/id_rsa")).Allowed.Should().BeFalse();

    [Theory]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("LS")]
    public async Task SearchTool_NoResolvedPath_Allowed(string tool)
    {
        // 可选 path 未提供 → None：安全层不施加路径约束（不替工具假设「默认就是 cwd」）。
        var result = await CheckAsync(tool);

        result.Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".env")]
    [InlineData("keys/authorized_keys")]
    public async Task WriteTool_ProtectedWriteSegment_Denied(string path)
        => (await CheckAsync("Write", path)).Allowed.Should().BeFalse();

    [Fact]
    public async Task DeleteTool_ProtectedWriteSegment_Denied()
    {
        // Delete 是 FileDelete 分类（不是 FileEdit/FileWrite），同样必须受写保护约束。
        var result = await CheckAsync("Delete", "backup/authorized_keys");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("authorized_keys");
    }

    [Fact]
    public async Task WriteTool_NoResolvedPath_Allowed()
    {
        // fail-closed 已上移到 SafetyInvariantMiddleware：本类只处理「有路径」与「无约束」，
        // 不得在此二次拒绝（否则 ApplyWorkspaceEdit 这类协议工具会被永久拦死）。
        (await CheckAsync("Write")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnresolvableRequest_AllowedAtInvariantLayer()
    {
        // 单一 Deny 点：Unresolvable 由中间件拒绝，不变量层不重复报错。
        var invariant = new FileSystemInvariant(WorkingDir);

        var result = await invariant.CheckAsync(
            new InvariantCheckRequest(
                "Write",
                new Dictionary<string, object?>(),
                ToolTargetPathResult.Unresolvable("missing filePath")),
            TestContext.Current.CancellationToken);

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task DynamicTool_SensitiveReadFile_Denied()
    {
        // 反证锚点：MCP 工具未声明文件分类（Dynamic），仍必须按解析出的目标路径套敏感读保护。
        var result = await CheckAsync("mcp__filesystem__read_file", "/home/u/.ssh/id_rsa");

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("id_rsa");
    }

    [Fact]
    public async Task DynamicTool_EnvFile_Denied()
        => (await CheckAsync("mcp__filesystem__read_file", ".env")).Allowed.Should().BeFalse();

    [Fact]
    public async Task DynamicTool_GitConfig_Allowed()
    {
        // Dynamic 不套写保护：读 .git/config 不在敏感读清单内，必须放行，
        // 否则「MCP 只读工具读 .git」这类合法调用会被误拦。
        (await CheckAsync("mcp__filesystem__read_file", ".git/config")).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("todos_add")]
    [InlineData("Task")]
    public async Task NonFileTools_WithPath_Allowed(string tool)
    {
        // provider 注入的会话状态工具与普通工具不触达用户工作区文件，路径检查必须跳过。
        (await CheckAsync(tool, "/outside/anything")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task MultiplePaths_AnyDenied_RejectsWholeCall()
    {
        var result = await CheckAsync("mcp__filesystem__read_file", "src/a.cs", ".env");

        result.Allowed.Should().BeFalse("多路径工具必须逐条校验，任一条命中敏感清单即拒绝");
    }
}
