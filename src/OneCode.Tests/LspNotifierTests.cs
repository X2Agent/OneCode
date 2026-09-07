using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Lsp;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

// LspNotifier — 诊断新鲜度判定（P2 回归锚定）：
//   基线 = didChange 发送时刻而非调用时刻（快路径推送不误判 stale）；
//   超时且无新版本诊断返回 null，不把上一版本 stale 诊断当结果误导模型。

public sealed class LspNotifierTests : IDisposable
{
    private const string TestUri = "file:///C:/proj/src/a.cs";
    private const string TestPath = @"C:\proj\src\a.cs";

    private readonly int _originalWaitTotalMs;
    private readonly int _originalPollIntervalMs;

    public LspNotifierTests()
    {
        // 轮询节奏注入（internal 静态，同 McpConnectionManager.AutoReconnectInterval 先例）：
        // 用 50ms 窗口替代 2000ms，测试内同步类内串行，Dispose 恢复。
        _originalWaitTotalMs = LspNotifier.WaitTotalMs;
        _originalPollIntervalMs = LspNotifier.PollIntervalMs;
        LspNotifier.WaitTotalMs = 50;
        LspNotifier.PollIntervalMs = 10;
    }

    public void Dispose()
    {
        LspNotifier.WaitTotalMs = _originalWaitTotalMs;
        LspNotifier.PollIntervalMs = _originalPollIntervalMs;
    }

    private static (LspNotifier Notifier, IEnhancedLspService Service, LspDiagnosticRegistry Registry) Create(
        bool hasServer, DateTimeOffset? didChangeUtc)
    {
        var service = Substitute.For<IEnhancedLspService>();
        service.HasRunningServer.Returns(hasServer);
        service.GetLastDidChangeUtc(Arg.Any<string>()).Returns(didChangeUtc);
        var registry = new LspDiagnosticRegistry();
        return (new LspNotifier(service, registry, NullLogger<LspNotifier>.Instance), service, registry);
    }

    private static JsonElement PublishParams(string uri, int severity, string message) =>
        JsonSerializer.Deserialize<JsonElement>($$"""
            {
              "uri": "{{uri}}",
              "diagnostics": [
                {
                  "range": { "start": { "line": 3, "character": 0 }, "end": { "line": 3, "character": 5 } },
                  "severity": {{severity}},
                  "message": "{{message}}"
                }
              ]
            }
            """);

    [Fact]
    public async Task GetDiagnosticsSummary_NoRunningServer_ReturnsNullImmediately()
    {
        var (notifier, _, registry) = Create(hasServer: false, didChangeUtc: DateTimeOffset.UtcNow);
        registry.ProcessDiagnostics("test", PublishParams(TestUri, 1, "CS0001 boom"));
        var sw = Stopwatch.StartNew();

        var summary = await notifier.GetDiagnosticsSummaryAsync(TestPath);

        summary.Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200), "无服务器时必须短路，不得白等轮询窗口");
    }

    [Fact]
    public async Task GetDiagnosticsSummary_FreshDiagnosticsAfterDidChange_ReturnedWithoutWaiting()
    {
        // 诊断在 didChange 之后推送（ProcessDiagnostics 时间戳 = 注入时刻 > 基线）——先查即中。
        var baseline = DateTimeOffset.UtcNow.AddSeconds(-2);
        var (notifier, _, registry) = Create(hasServer: true, didChangeUtc: baseline);
        registry.ProcessDiagnostics("test", PublishParams(TestUri, 1, "CS0001 boom"));
        var sw = Stopwatch.StartNew();

        var summary = await notifier.GetDiagnosticsSummaryAsync(TestPath);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "fresh 诊断先查即中，不空等轮询窗口");
        summary.Should().NotBeNull();
        summary.Should().Contain("1 error(s)");
        summary.Should().Contain("CS0001 boom");
    }

    [Fact]
    public async Task GetDiagnosticsSummary_OnlyStaleDiagnostics_TimesOutReturningNull()
    {
        // 回归锚定（P2 核心语义）：基线晚于既有诊断（模拟上一版本的推送），等待窗口内
        // 没有新版本诊断到达 → 必须返回 null，不得把 stale 诊断当结果。
        var (notifier, service, registry) = Create(hasServer: true, didChangeUtc: null);
        registry.ProcessDiagnostics("test", PublishParams(TestUri, 1, "old version diagnostic"));
        service.GetLastDidChangeUtc(Arg.Any<string>())
            .Returns(DateTimeOffset.UtcNow.AddMilliseconds(50)); // 基线晚于注入的诊断
        var sw = Stopwatch.StartNew();

        var summary = await notifier.GetDiagnosticsSummaryAsync(TestPath);

        summary.Should().BeNull("超时且无新版本诊断时不得返回 stale 旧版本");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40), "stale 场景应实际等待轮询窗口确认");
    }

    [Fact]
    public async Task GetDiagnosticsSummary_NeverDidChange_FallsBackToCallTimeBaseline()
    {
        // 只读分析路径（文件从未经过 NotifyFileUpdatedAsync）：无 didChange 基线 →
        // 回落调用时刻，既有诊断按定义旧于基线 → 返回 null 而非无法判断版本的数据。
        var (notifier, _, registry) = Create(hasServer: true, didChangeUtc: null);
        registry.ProcessDiagnostics("test", PublishParams(TestUri, 1, "existing diagnostic"));

        var summary = await notifier.GetDiagnosticsSummaryAsync(TestPath);

        summary.Should().BeNull();
    }

    [Fact]
    public async Task GetDiagnosticsSummary_ServerIndexing_ExtendsWaitBudget()
    {
        // 大项目首次索引：新诊断在固定窗口（注入后为 50ms）之后才推送，但服务器
        // IsIndexing=true → 预算延长到 IndexingWaitTotalMs，迟到的新诊断不被假阴性放弃。
        var (notifier, service, registry) = Create(hasServer: true, didChangeUtc: DateTimeOffset.UtcNow.AddSeconds(-2));
        service.IsIndexing.Returns(true);
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            registry.ProcessDiagnostics("test", PublishParams(TestUri, 1, "late fresh diagnostic"));
        });

        var summary = await notifier.GetDiagnosticsSummaryAsync(TestPath);

        summary.Should().NotBeNull("indexing 中等待预算延长，超过固定窗口的迟到新诊断不应被放弃");
        summary.Should().Contain("1 error(s)");
    }
}
