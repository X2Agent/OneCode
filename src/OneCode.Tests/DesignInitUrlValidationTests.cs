using OneCode.App.Commands;

namespace OneCode.Tests;

/// <summary>
/// <c>/design-init</c> 的 URL 校验：文档承诺「只接受公网 http(s) 地址」，
/// 主机名黑名单之外还必须拦住 IP 字面量与整数形式——否则 127.0.0.1 / 192.168.* /
/// 2130706433 都能绕过。反证：边界地址（172.15 / 172.32 / 192.169）必须放行，
/// 防止把整个网段一刀切。
/// </summary>
public sealed class DesignInitUrlValidationTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("https://sub.example.com:8443/design")]
    [InlineData("http://172.15.0.1")]
    [InlineData("http://172.32.0.1")]
    [InlineData("http://192.169.0.1")]
    [InlineData("http://11.0.0.1")]
    [InlineData("http://8.8.8.8")]
    [InlineData("http://[2001:db8::1]")]
    public void PublicUrls_AreAccepted(string url)
        => DesignInitCommand.IsValidUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://app.internal")]
    [InlineData("http://printer.local")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.1.2.3")]
    [InlineData("http://0.0.0.0")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://172.31.255.255")]
    [InlineData("http://192.168.1.10")]
    [InlineData("http://169.254.1.1")]
    [InlineData("http://[::1]")]
    [InlineData("http://[fe80::1]")]
    [InlineData("http://[fd00::1]")]
    [InlineData("http://2130706433")]
    [InlineData("http://user:pass@example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    public void LocalPrivateAndNonHttpTargets_AreRejected(string url)
        => DesignInitCommand.IsValidUrl(url).Should().BeFalse();
}
