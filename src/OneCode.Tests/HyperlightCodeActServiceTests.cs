using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

public sealed class HyperlightCodeActServiceTests
{
    [Fact]
    public void TryCreateProvider_RuntimeUnavailable_ReturnsNull()
    {
        var probe = Substitute.For<IHyperlightRuntimeProbe>();
        probe.IsAvailable().Returns(false);
        var service = new HyperlightCodeActService(
            NullLogger<HyperlightCodeActService>.Instance, probe);

        var provider = service.TryCreateProvider(Path.GetTempPath());

        provider.Should().BeNull();
    }

    [Fact]
    public void TryCreateProvider_RuntimeAvailable_ReturnsProvider()
    {
        // 构造函数惰性：不初始化 VM，测试环境可安全构造 provider。
        var probe = Substitute.For<IHyperlightRuntimeProbe>();
        probe.IsAvailable().Returns(true);
        var service = new HyperlightCodeActService(
            NullLogger<HyperlightCodeActService>.Instance, probe);

        var provider = service.TryCreateProvider(Path.GetTempPath());

        provider.Should().NotBeNull();
    }

    [Fact]
    public void BuildOptions_ExistingDirectory_SetsHostInputDirectoryOnly()
    {
        var tempDir = Directory.CreateTempSubdirectory("onecode-hyperlight-").FullName;
        try
        {
            var options = HyperlightCodeActService.BuildOptions(tempDir);

            options.ApprovalMode.Should().Be(CodeActApprovalMode.AlwaysRequire);
            options.HostInputDirectory.Should().Be(tempDir);
            options.FileMounts.Should().BeNull();
            options.Tools.Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildOptions_MissingDirectory_LeavesHostInputDirectoryNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var options = HyperlightCodeActService.BuildOptions(missing);

        options.HostInputDirectory.Should().BeNull();
        options.FileMounts.Should().BeNull();
        options.Tools.Should().BeNull();
    }
}
