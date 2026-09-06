using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// 子命令级忙碌标签（<see cref="OneCode.Core.Commands.ICommand.GetSubcommandProgressMessage"/>）的行为锚点。
/// 背景：官方 MCP registry 的 search 端点较慢（实测 18~26s 返回），/mcp search 执行期间
/// AgentStatusBar 必须显示专用忙碌标签，否则用户会误以为卡死；install 按目标形态细分——
/// 序号/限定名只做一次 GetLatest（秒级），短名可能回退一次慢速 search；本地子命令
/// （list/get 等）瞬时完成，须返回 null 回退到默认"执行 /mcp"标签。
/// </summary>
public sealed class SubcommandProgressMessageTests
{
    [Fact]
    public void McpCommand_Search_ReturnsSearchingLabel()
    {
        CreateMcpCommand().GetSubcommandProgressMessage(["search", "file"])
            .Should().Be("searching MCP registry", "search 访问官方 registry（18~26s），必须有忙碌反馈");
    }

    [Theory]
    [InlineData("1")]                  // search 结果编号（纯数字）
    [InlineData("io.github.x/y")]      // 完整限定名
    public void McpCommand_Install_FastTarget_ReturnsInstallingLabel(string target)
    {
        CreateMcpCommand().GetSubcommandProgressMessage(["install", target])
            .Should().Be("installing from MCP registry", "序号/限定名只做一次 GetLatest（秒级），无需 search 文案");
    }

    [Fact]
    public void McpCommand_Install_ShortName_ReturnsSearchingLabel()
    {
        CreateMcpCommand().GetSubcommandProgressMessage(["install", "everything-mcp"])
            .Should().Be("searching MCP registry", "短名回退会触发一次慢速 registry search（18~26s），与 search 同等待预期");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("remove")]
    public void McpCommand_LocalSubcommands_ReturnNull_FallsBackToDefaultLabel(string subcommand)
    {
        CreateMcpCommand().GetSubcommandProgressMessage([subcommand])
            .Should().BeNull("本地操作瞬时完成，回退默认'执行 /mcp'标签即可");
    }

    [Fact]
    public void McpCommand_NoArgs_ReturnsNull()
    {
        CreateMcpCommand().GetSubcommandProgressMessage([]).Should().BeNull();
    }

    /// <summary>GetSubcommandProgressMessage 不触网，客户端用最简离线 stub 工厂构造即可。</summary>
    private static McpCommand CreateMcpCommand() => new(
        Substitute.For<IMcpConnectionManager>(),
        new OfficialMcpRegistryClient(new OfflineHttpClientFactory()),
        new McpMultiScopeConfigLoader(Substitute.For<ILogger<McpMultiScopeConfigLoader>>()),
        Substitute.For<ILogger<McpCommand>>());

    private sealed class OfflineHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
