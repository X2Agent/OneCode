using Microsoft.Extensions.Logging;
using OneCode.App.Logging;

namespace OneCode.Tests;

/// <summary>
/// Covers <see cref="DebugLogConfig.Resolve"/> — the single ONECODE_LOG_LEVEL
/// (off|debug|trace) resolution. Locks the two previous flaws: trace-without-debug
/// was a no-op, and debug logging could not be disabled in a DEBUG build.
/// </summary>
public sealed class DebugLogConfigTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    public void Resolve_UnsetOrUnknown_UsesBuildDefault(string? raw)
    {
        DebugLogConfig.Resolve(isDebugBuild: false, raw).Enabled.Should().BeFalse();

        var debugBuild = DebugLogConfig.Resolve(isDebugBuild: true, raw);
        debugBuild.Enabled.Should().BeTrue();
        debugBuild.MinimumLevel.Should().Be(LogLevel.Debug);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("0")]
    [InlineData("false")]
    public void Resolve_Off_DisablesEvenInDebugBuild(string raw)
    {
        DebugLogConfig.Resolve(isDebugBuild: true, raw).Enabled.Should().BeFalse();
        DebugLogConfig.Resolve(isDebugBuild: false, raw).Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("DEBUG")]
    [InlineData("1")]
    public void Resolve_Debug_EnablesAtDebugRegardlessOfBuild(string raw)
    {
        AssertEnabledAt(DebugLogConfig.Resolve(isDebugBuild: false, raw), LogLevel.Debug);
        AssertEnabledAt(DebugLogConfig.Resolve(isDebugBuild: true, raw), LogLevel.Debug);
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("TRACE")]
    [InlineData("2")]
    public void Resolve_Trace_EnablesAtTraceRegardlessOfBuild(string raw)
    {
        AssertEnabledAt(DebugLogConfig.Resolve(isDebugBuild: false, raw), LogLevel.Trace);
        AssertEnabledAt(DebugLogConfig.Resolve(isDebugBuild: true, raw), LogLevel.Trace);
    }

    private static void AssertEnabledAt(DebugLogConfig cfg, LogLevel level)
    {
        cfg.Enabled.Should().BeTrue();
        cfg.MinimumLevel.Should().Be(level);
    }
}
