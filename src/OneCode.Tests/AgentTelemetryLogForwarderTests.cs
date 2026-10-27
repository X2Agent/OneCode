using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// <see cref="AgentTelemetryLogForwarder"/> 行为测试：MAF <see cref="ActivitySource"/> 的 span
/// 停止时转发进 <see cref="ILogger"/>（名称、耗时、状态），压缩 span 附带 compaction.* 标签；
/// Dispose 后不再监听。
/// </summary>
public sealed class AgentTelemetryLogForwarderTests
{
    [Fact]
    public void AgentSpanStopped_ForwardsNameDurationAndStatus()
    {
        var logger = new RecordingLogger();
        using var source = new ActivitySource(AgentTelemetryLogForwarder.AgentSourceName);
        using var forwarder = new AgentTelemetryLogForwarder(new SingleLoggerFactory(logger));

        var activity = source.StartActivity("agent run");
        activity.Should().NotBeNull("转发器订阅后 StartActivity 必须返回实例（否则整条遥测链零输出）");
        activity!.SetStatus(ActivityStatusCode.Ok);
        activity.Stop();

        var entry = logger.Entries.Should().ContainSingle("每个停止的 span 恰好产生一条日志").Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("agent run", "span 名称必须进日志以便定位是哪一级遥测");
        entry.Message.Should().Contain("状态 ok", "正常结束的 span 状态必须可见");
    }

    [Fact]
    public void CompactionSpanStopped_ForwardsCompactionTags()
    {
        var logger = new RecordingLogger();
        using var source = new ActivitySource(AgentTelemetryLogForwarder.CompactionSourceName);
        using var forwarder = new AgentTelemetryLogForwarder(new SingleLoggerFactory(logger));

        var activity = source.StartActivity("compaction.compact");
        activity.Should().NotBeNull();
        activity!.SetTag("compaction.before.tokens", 12_000L);
        activity.SetTag("compaction.after.tokens", 4_000L);
        activity.SetTag("compaction.before.messages", 40L);
        activity.SetTag("compaction.after.messages", 9L);
        activity.Stop();

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().Contain("compaction.compact");
        message.Should().Contain("before.tokens=12000", "压缩前后 token 对比是调压缩问题的关键证据");
        message.Should().Contain("after.tokens=4000");
        message.Should().Contain("before.messages=40");
        message.Should().Contain("after.messages=9");
    }

    [Fact]
    public void Dispose_StopsForwarding()
    {
        var logger = new RecordingLogger();
        using var source = new ActivitySource(AgentTelemetryLogForwarder.AgentSourceName);
        var forwarder = new AgentTelemetryLogForwarder(new SingleLoggerFactory(logger));
        forwarder.Dispose();

        _ = source.StartActivity("after dispose");

        logger.Entries.Should().BeEmpty("转发器释放后不得再产生日志");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose() { }
    }
}
