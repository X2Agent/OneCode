using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Skills;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="SkillProviderFactory"/> — the per-run skills provider.
///
/// <para>
/// The provider is built on every agent run so that MCP servers which connect (or disconnect)
/// after startup are reflected without rebuilding or swapping anything. These tests pin that
/// contract: each call yields a usable provider, and connection-pool state is re-read rather
/// than captured once.
/// </para>
/// </summary>
public sealed class SkillProviderFactoryTests : IDisposable
{
    /// <summary>Project skills live at <c>&lt;workingDir&gt;/.onecode/skills</c>.</summary>
    private readonly string _projectRoot;
    private readonly string _skillsDir;

    public SkillProviderFactoryTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"SkillProviderFactoryTests_{Guid.NewGuid():N}");
        _skillsDir = Path.Combine(_projectRoot, ".onecode", Constants.Subdirs.Skills);
        Directory.CreateDirectory(_skillsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { /* best effort */ }
    }

    private SkillProviderFactory CreateSut(IMcpConnectionManager mcpManager)
        => new(new SkillCatalog(_projectRoot), mcpManager, NullLoggerFactory.Instance);

    private void WriteSkill(string name, string description, string body)
    {
        var skillDir = Path.Combine(_skillsDir, name);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");
    }

    /// <summary>
    /// Bundled skills are always present, so a provider built with no connected MCP servers and no
    /// files on disk still advertises skills. Pins that the factory wires bundled skills in.
    /// </summary>
    [Fact]
    public async Task Create_AdvertisesBundledSkills()
    {
        var mcpManager = Substitute.For<IMcpConnectionManager>();
        mcpManager.GetConnectedClients().Returns([]);
        var sut = CreateSut(mcpManager);

        var context = await InvokeAsync(sut.Create());

        context.Instructions.Should().NotBeNullOrWhiteSpace();
        // 无磁盘 skill、无 MCP 服务器时，Instructions 必须由 bundled skills 构成并包含其名称。
        context.Instructions.Should().Contain("debug", "bundled skill 名称必须出现在 provider 指令中");
        context.Instructions.Should().Contain("verify");
    }

    /// <summary>
    /// The connection pool must be consulted per call: this is what makes a server that connects
    /// after startup visible on the next run without any provider rebuild.
    /// </summary>
    [Fact]
    public void Create_ReadsConnectionPoolOnEveryCall()
    {
        var mcpManager = Substitute.For<IMcpConnectionManager>();
        mcpManager.GetConnectedClients().Returns([]);
        var sut = CreateSut(mcpManager);

        sut.Create();
        sut.Create();

        mcpManager.Received(2).GetConnectedClients();
    }

    /// <summary>
    /// Two runs must not share a provider instance; otherwise MAF's provider-level skill cache
    /// would freeze the skill set for the lifetime of the process.
    /// </summary>
    [Fact]
    public void Create_ReturnsNewInstancePerCall()
    {
        var mcpManager = Substitute.For<IMcpConnectionManager>();
        mcpManager.GetConnectedClients().Returns([]);
        var sut = CreateSut(mcpManager);

        sut.Create().Should().NotBeSameAs(sut.Create());
    }

    /// <summary>
    /// A skill added to a watched directory after the first call must be visible to the next call.
    /// </summary>
    [Fact]
    public async Task Create_SeesSkillFileAddedAfterFirstCall()
    {
        var mcpManager = Substitute.For<IMcpConnectionManager>();
        mcpManager.GetConnectedClients().Returns([]);
        var sut = CreateSut(mcpManager);

        var before = await InvokeAsync(sut.Create());

        WriteSkill("late-skill", "Added after the first provider was built", "Late body.");

        var after = await InvokeAsync(sut.Create());

        after.Instructions.Should().NotBe(before.Instructions);
        after.Instructions.Should().Contain("late-skill");
    }

    /// <summary>
    /// Servers without a usable SDK client are skipped without throwing. <see cref="IMcpClient"/>
    /// substitutes are not <see cref="McpClient"/>, so they exercise that guard.
    /// </summary>
    [Fact]
    public async Task Create_SkipsConnectedClientsThatAreNotSdkBacked()
    {
        var mcpManager = Substitute.For<IMcpConnectionManager>();
        mcpManager.GetConnectedClients().Returns([("not-sdk-backed", Substitute.For<IMcpClient>())]);
        var sut = CreateSut(mcpManager);

        var act = () => sut.Create();

        act.Should().NotThrow();

        // 不可用的客户端被跳过后，Create 的产物必须仍由 bundled skills 构成，
        // 且不得把失败客户端的标识当作 skill 注入。
        var context = await InvokeAsync(sut.Create());
        context.Instructions.Should().NotBeNullOrWhiteSpace();
        context.Instructions.Should().Contain("debug", "跳过无效客户端后 bundled skills 仍然生效");
        context.Instructions.Should().NotContain("not-sdk-backed");
    }

    private static async Task<AIContext> InvokeAsync(AgentSkillsProvider provider)
    {
        var context = new AIContext();
        var invokingContext = new AIContextProvider.InvokingContext(
            TestAgents.CreateCountingAgent("skill-provider-test"),
            session: null,
            context);

        return await provider.InvokingAsync(invokingContext, TestContext.Current.CancellationToken);
    }
}
