using System.Net;
using System.Reflection;
using OneCode.App.Tools;

namespace OneCode.Tests;

public sealed class WebFetchToolSsrfTests
{
    /// <summary>
    /// ValidateUrl is private static — invoke it via reflection so the SSRF
    /// guard can be tested in isolation without an HTTP client.
    /// </summary>
    private static bool InvokeValidateUrl(string url)
    {
        var method = typeof(WebFetchTool).GetMethod(
            "ValidateUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, new object[] { url })!;
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    public void IsPrivateOrLocalAddress_IPv4Private_ReturnsTrue(string ip)
    {
        var address = IPAddress.Parse(ip);

        WebFetchTool.IsPrivateOrLocalAddressPublic(address).Should().BeTrue();
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    public void IsPrivateOrLocalAddress_IPv6Private_ReturnsTrue(string ip)
    {
        var address = IPAddress.Parse(ip);

        WebFetchTool.IsPrivateOrLocalAddressPublic(address).Should().BeTrue();
    }

    [Theory]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    public void IsPrivateOrLocalAddress_Outside172PrivateRange_ReturnsFalse(string ip)
    {
        // 172.16.0.0/12 covers only second octet 16..31; 15 and 32 must pass.
        var address = IPAddress.Parse(ip);

        WebFetchTool.IsPrivateOrLocalAddressPublic(address).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    public void IsPrivateOrLocalAddress_PublicIPv4_ReturnsFalse(string ip)
    {
        var address = IPAddress.Parse(ip);

        WebFetchTool.IsPrivateOrLocalAddressPublic(address).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://127.0.0.1/")]
    public void ValidateUrl_IPv4PrivateAddresses_ReturnsFalse(string url)
    {
        InvokeValidateUrl(url).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd00::1]/")]
    public void ValidateUrl_IPv6PrivateAddresses_ReturnsFalse(string url)
    {
        InvokeValidateUrl(url).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://foo.internal/")]
    [InlineData("http://foo.local/")]
    [InlineData("http://foo.localhost/")]
    public void ValidateUrl_LocalhostAndInternalNames_ReturnsFalse(string url)
    {
        InvokeValidateUrl(url).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://172.15.0.1/")]
    [InlineData("http://172.32.0.1/")]
    public void ValidateUrl_Outside172PrivateRange_ReturnsTrue(string url)
    {
        InvokeValidateUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("https://8.8.8.8/")]
    [InlineData("http://1.1.1.1/")]
    [InlineData("https://github.com/")]
    public void ValidateUrl_PublicAddresses_ReturnsTrue(string url)
    {
        InvokeValidateUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("ftp://8.8.8.8/")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void ValidateUrl_InvalidSchemeOrFormat_ReturnsFalse(string url)
    {
        InvokeValidateUrl(url).Should().BeFalse();
    }

    [Fact]
    public void ValidateUrl_UrlWithCredentials_ReturnsFalse()
    {
        InvokeValidateUrl("http://user:pass@8.8.8.8/").Should().BeFalse();
    }

    [Fact]
    public void ValidateUrl_OverlongUrl_ReturnsFalse()
    {
        var longHost = new string('a', 2100);
        InvokeValidateUrl($"http://{longHost}.com/").Should().BeFalse();
    }
}
