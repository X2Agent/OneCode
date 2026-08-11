using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Tools;

namespace OneCode.Tests;

public class GenericVerificationProviderTests
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GenericVerificationProvider> _logger;

    public GenericVerificationProviderTests()
    {
        _processRunner = Substitute.For<IProcessRunner>();
        _logger = Substitute.For<ILogger<GenericVerificationProvider>>();
    }

    [Theory]
    [InlineData("foo.cs")]
    [InlineData("foo.vb")]
    [InlineData("foo.fs")]
    [InlineData("foo.ts")]
    [InlineData("foo.tsx")]
    [InlineData("foo.go")]
    [InlineData("foo.rs")]
    public void IsSourceFile_SupportedExtensions_ReturnsTrue(string path)
    {
        var provider = new GenericVerificationProvider(_processRunner, _logger);
        provider.IsSourceFile(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("foo.py")]
    [InlineData("foo.md")]
    [InlineData("foo.json")]
    [InlineData("foo.java")]
    [InlineData("README")]
    public void IsSourceFile_UnsupportedExtensions_ReturnsFalse(string path)
    {
        var provider = new GenericVerificationProvider(_processRunner, _logger);
        provider.IsSourceFile(path).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_NoMatchingProfile_ReturnsSuccessImmediately()
    {
        var provider = new GenericVerificationProvider(_processRunner, _logger);
        // .py 文件不在任何 profile 中
        var result = await provider.VerifyAsync("/tmp", ["foo.py"], CancellationToken.None);
        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        // 不应该调用任何进程
        await _processRunner.DidNotReceive().ExecuteWithTimeoutAsync(
            Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>());
    }

    [Fact]
    public async Task VerifyAsync_NoProjectMarker_ReturnsSuccess()
    {
        var provider = new GenericVerificationProvider(_processRunner, _logger);
        // .cs 文件但没有 .csproj 标记
        var result = await provider.VerifyAsync("/tmp", ["foo.cs"], CancellationToken.None);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_CommandNotAvailable_SkipsAndReturnsSuccess()
    {
        _processRunner.CommandExistsAsync("dotnet").Returns(false);
        var provider = new GenericVerificationProvider(_processRunner, _logger);
        var tempDir = Path.Combine(Path.GetTempPath(), "onecode_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project />");
        try
        {
            var result = await provider.VerifyAsync(tempDir, ["foo.cs"], CancellationToken.None);
            result.Success.Should().BeTrue();
        }
        finally
        {
            File.Delete(Path.Combine(tempDir, "test.csproj"));
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_DotNetBuildFails_ParsesErrors()
    {
        _processRunner.CommandExistsAsync("dotnet").Returns(true);
        var errorOutput = "Program.cs(10,5): error CS0103: The name 'foo' does not exist in the current context";
        _processRunner.ExecuteWithTimeoutAsync(
            "dotnet", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new ProcessResult(1, "", errorOutput, false));

        var provider = new GenericVerificationProvider(_processRunner, _logger);
        var tempDir = Path.Combine(Path.GetTempPath(), "onecode_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project />");
        try
        {
            var result = await provider.VerifyAsync(tempDir, ["Program.cs"], CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Errors.Should().HaveCount(1);
            result.Errors[0].File.Should().Be("Program.cs");
            result.Errors[0].Line.Should().Be(10);
            result.Errors[0].Column.Should().Be(5);
            result.Errors[0].Severity.Should().Be("error");
            result.Errors[0].Message.Should().Contain("CS0103");
            result.Errors[0].Message.Should().Contain("foo");
        }
        finally
        {
            File.Delete(Path.Combine(tempDir, "test.csproj"));
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_RustShortFormat_ParsesErrors()
    {
        _processRunner.CommandExistsAsync("cargo").Returns(true);
        var errorOutput = "error[E0425]: cannot find value `foo` in this scope --> src/main.rs:2:5";
        _processRunner.ExecuteWithTimeoutAsync(
            "cargo", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new ProcessResult(1, "", errorOutput, false));

        var provider = new GenericVerificationProvider(_processRunner, _logger);
        var tempDir = Path.Combine(Path.GetTempPath(), "onecode_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Cargo.toml"), "[package]");
        try
        {
            var result = await provider.VerifyAsync(tempDir, ["src/main.rs"], CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Errors.Should().HaveCount(1);
            result.Errors[0].File.Should().Be("src/main.rs");
            result.Errors[0].Line.Should().Be(2);
            result.Errors[0].Column.Should().Be(5);
            result.Errors[0].Severity.Should().Be("error");
            result.Errors[0].Message.Should().Contain("E0425");
        }
        finally
        {
            File.Delete(Path.Combine(tempDir, "Cargo.toml"));
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_BuildSucceeds_ReturnsSuccess()
    {
        _processRunner.CommandExistsAsync("dotnet").Returns(true);
        _processRunner.ExecuteWithTimeoutAsync(
            "dotnet", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new ProcessResult(0, "", "", false));

        var provider = new GenericVerificationProvider(_processRunner, _logger);
        var tempDir = Path.Combine(Path.GetTempPath(), "onecode_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project />");
        try
        {
            var result = await provider.VerifyAsync(tempDir, ["Program.cs"], CancellationToken.None);
            result.Success.Should().BeTrue();
            result.Errors.Should().BeEmpty();
        }
        finally
        {
            File.Delete(Path.Combine(tempDir, "test.csproj"));
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task VerifyBuildAndTestsAsync_BuildSucceeds_RunsConfiguredTests()
    {
        _processRunner.CommandExistsAsync("dotnet").Returns(true);
        _processRunner.ExecuteWithTimeoutAsync(
                "dotnet", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new ProcessResult(0, "", "", false));

        var provider = new GenericVerificationProvider(_processRunner, _logger);
        var tempDir = Path.Combine(Path.GetTempPath(), "onecode_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project />");
        try
        {
            var result = await provider.VerifyBuildAndTestsAsync(tempDir, ["Program.cs"], CancellationToken.None);

            result.Success.Should().BeTrue();
            await _processRunner.Received(1).ExecuteWithTimeoutAsync(
                "dotnet", Arg.Is<string[]>(args => args.SequenceEqual(new[] { "test", "--no-build" })),
                tempDir, Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(Path.Combine(tempDir, "test.csproj"));
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task VerifyBuildAndTestsAsync_TestFails_ReturnsFailure()
    {
        _processRunner.CommandExistsAsync("dotnet").Returns(true);
        _processRunner.ExecuteWithTimeoutAsync(
                "dotnet", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(call =>
            {
                var args = call.ArgAt<string[]>(1);
                return args.Contains("test")
                    ? new ProcessResult(1, "Tests Failed", "", false)
                    : new ProcessResult(0, "", "", false);
            });

        var provider = new GenericVerificationProvider(_processRunner, _logger);
        var tempDir = Path.Combine(Path.GetTempPath(), "onecode_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project />");
        try
        {
            var result = await provider.VerifyBuildAndTestsAsync(tempDir, ["Program.cs"], CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle(error => error.File == "(test)");
        }
        finally
        {
            File.Delete(Path.Combine(tempDir, "test.csproj"));
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_Timeout_ReturnsFailure()
    {
        _processRunner.CommandExistsAsync("dotnet").Returns(true);
        _processRunner.ExecuteWithTimeoutAsync(
            "dotnet", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new ProcessResult(-1, "", "", TimedOut: true));

        var provider = new GenericVerificationProvider(_processRunner, _logger);
        var tempDir = Path.Combine(Path.GetTempPath(), "onecode_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.csproj"), "<Project />");
        try
        {
            var result = await provider.VerifyAsync(tempDir, ["Program.cs"], CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Errors.Should().HaveCount(1);
            result.Errors[0].Message.Should().Contain("timed out");
        }
        finally
        {
            File.Delete(Path.Combine(tempDir, "test.csproj"));
            Directory.Delete(tempDir);
        }
    }
}
