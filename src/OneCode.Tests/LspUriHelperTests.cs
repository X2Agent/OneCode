using OneCode.Infrastructure.Text;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LspUriHelper"/> — covers file:// URI construction
/// from Windows/Unix paths and the reverse URI-to-path conversion.
/// </summary>
public sealed class LspUriHelperTests
{
    // BuildFileUri

    [Theory]
    [InlineData("C:\\Users\\test\\file.cs", "file:///C:/Users/test/file.cs")]
    [InlineData("D:\\proj\\src.cs", "file:///D:/proj/src.cs")]
    public void BuildFileUri_WindowsDrivePath_ProducesTripleSlashUri(string input, string expected)
    {
        LspUriHelper.BuildFileUri(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/home/user/file.cs", "file:///home/user/file.cs")]
    [InlineData("/tmp/test.py", "file:///tmp/test.py")]
    public void BuildFileUri_UnixAbsolutePath_ProducesTripleSlashUri(string input, string expected)
    {
        LspUriHelper.BuildFileUri(input).Should().Be(expected);
    }

    [Fact]
    public void BuildFileUri_AlreadyUri_ReturnedUnchanged()
    {
        const string uri = "file:///C:/Users/test/file.cs";
        LspUriHelper.BuildFileUri(uri).Should().Be(uri);
    }

    // UriToFilePath

    [Fact]
    public void UriToFilePath_WindowsTripleSlashUri_ConvertsToNativePath()
    {
        var uri = "file:///C:/Users/test/file.cs";
        // Path.Combine("C:", ...) treats "C:" as drive-relative and omits the separator,
        // so build the expected value explicitly to match the helper's output.
        var expected = $"C:{Path.DirectorySeparatorChar}Users{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}file.cs";
        LspUriHelper.UriToFilePath(uri).Should().Be(expected);
    }

    [Fact]
    public void UriToFilePath_UnixTripleSlashUri_ConvertsToUnixPath()
    {
        var uri = "file:///home/user/file.cs";
        // On Windows, forward slashes are converted to DirectorySeparatorChar.
        var expected = uri["file:///".Length..].Replace('/', Path.DirectorySeparatorChar);
        LspUriHelper.UriToFilePath(uri).Should().Be(expected);
    }

    [Fact]
    public void UriToFilePath_NonFileUri_ReturnedUnchanged()
    {
        const string uri = "https://example.com/resource";
        LspUriHelper.UriToFilePath(uri).Should().Be(uri);
    }

    [Fact]
    public void UriToFilePath_EmptyString_ReturnedUnchanged()
    {
        LspUriHelper.UriToFilePath("").Should().Be("");
    }

    [Fact]
    public void UriToFilePath_NullInput_ReturnsNull()
    {
        LspUriHelper.UriToFilePath(null!).Should().BeNull();
    }
}
