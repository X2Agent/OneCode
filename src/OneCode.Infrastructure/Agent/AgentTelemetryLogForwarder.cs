using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 把 MAF（Microsoft.Agents.AI）的 <see cref="ActivitySource"/> 遥测转发进宿主本地日志（<see cref="ILogger"/>）。
/// </summary>
/// <remarks>
/// <para>
/// Harness 两级遥测（agent 级 <c>OpenTelemetryAgent</c>、chat client 级 <c>OpenTelemetryChatClient</c>）与
/// 压缩遥测默认已开启，但 <see cref="ActivitySource"/> 没有订阅者时 <see cref="ActivitySource.StartActivity"/>
/// 返回 null、整条链零输出——本类型就是那个订阅者：不引入 OpenTelemetry SDK，只把 span 的名称、
/// 耗时、状态与压缩标签写进 <see cref="ILogger"/>，随宿主既有双通道（控制台 + 会话调试文件）落盘。
/// </para>
/// <para>
/// 级别边界（有意沿用 <c>DebugFileLoggerProvider.MinimumLevel</c> 的现有策略，不在本类型内另设开关）：
/// DEBUG 构建遥测行进控制台与 <c>{configDir}/debug/{sessionId}.log</c>；Release 构建文件通道只记 Warning+，
/// 遥测行在 Release 下静默。
/// </para>
/// </remarks>
public sealed class AgentTelemetryLogForwarder : IDisposable
{
    /// <summary>agent / chat client 两级遥测的默认源名（MAF <c>OpenTelemetryAgent.DefaultSourceName</c>，1.26.0 DLL 字符串取证）。</summary>
    public const string AgentSourceName = "Experimental.Microsoft.Agents.AI";

    /// <summary>压缩遥测源名（MAF <c>CompactionTelemetry</c>，1.26.0 DLL 字符串取证；span 名为 compaction.* 前缀）。</summary>
    public const string CompactionSourceName = "Microsoft.Agents.AI";

    private readonly ILogger _logger;
    private readonly ActivityListener _listener;

    public AgentTelemetryLogForwarder(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger("OneCode.Agent.Telemetry");

        _listener = new ActivityListener
        {
            ActivityStopped = OnActivityStopped,
            ShouldListenTo = source =>
                source.Name == AgentSourceName || source.Name == CompactionSourceName,
            // 不设 Sample 时默认按不采样处理，StartActivity 直接返回 null——必须显式全采样。
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private void OnActivityStopped(Activity activity)
    {
        var durationMs = activity.Duration.TotalMilliseconds;
        var status = activity.Status switch
        {
            ActivityStatusCode.Ok => "ok",
            ActivityStatusCode.Error => $"error: {activity.StatusDescription}",
            _ => "unset",
        };

        if (activity.Source.Name == CompactionSourceName)
        {
            _logger.LogInformation(
                "MAF 压缩遥测 {SpanName} 耗时 {DurationMs:F1}ms 状态 {Status} {CompactionTags}",
                activity.OperationName,
                durationMs,
                status,
                FormatCompactionTags(activity));
            return;
        }

        _logger.LogInformation(
            "MAF 遥测 {SpanName} 耗时 {DurationMs:F1}ms 状态 {Status}",
            activity.OperationName,
            durationMs,
            status);
    }

    /// <summary>抽取压缩 span 的关键标签（前后 token / 消息数、耗时），供日志按结构化字段检索。</summary>
    private static string FormatCompactionTags(Activity activity)
    {
        string? Tag(string name) => activity.GetTagItem(name)?.ToString();

        var parts = new[]
        {
            ("before.tokens", Tag("compaction.before.tokens")),
            ("after.tokens", Tag("compaction.after.tokens")),
            ("before.messages", Tag("compaction.before.messages")),
            ("after.messages", Tag("compaction.after.messages")),
            ("duration_ms", Tag("compaction.duration_ms")),
        }.Where(pair => pair.Item2 is not null)
         .Select(pair => $"{pair.Item1}={pair.Item2}");

        var joined = string.Join(' ', parts);
        return joined.Length == 0 ? "（无标签）" : joined;
    }

    public void Dispose() => _listener.Dispose();
}
