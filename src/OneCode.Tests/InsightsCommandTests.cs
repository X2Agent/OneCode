using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Session;
using System.Diagnostics;

namespace OneCode.Tests;

/// <summary>
/// <see cref="InsightsCommand"/> 对会话事件文件的解析与聚合契约。
/// </summary>
/// <remarks>
/// 事件文件由真实 <see cref="FileSessionEventStore"/> 写出（而非手搓 JSON），
/// 以保证测试覆盖「存储写入端 ↔ insights 读取端」的目录、字段名与聚合契约。
/// 历史教训：本命令曾扫描不存在的 <c>~/.onecode/sessions</c> 目录，并断言
/// <c>session_header</c> / 顶层 <c>total_usage</c> 等**从不存在的**字段，
/// 而当时没有任何测试覆盖它，缺陷得以长期潜伏。
/// 规则见 <c>src/OneCode.Tests/AGENTS.md</c>「契约测试：让写入路径与读取路径互相验证」。
/// </remarks>
public sealed class InsightsCommandTests : IDisposable
{
    /// <summary>模拟用户主目录；命令与事件存储都以此为基准解析同一目录。</summary>
    private readonly string _home;
    private readonly FileSessionEventStore _store;

    public InsightsCommandTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"Insights_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        _store = new FileSessionEventStore(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"Cleanup failed: {ex.Message}"); }
    }

    /// <summary>
    /// 主契约：真实写出的事件被解析后，token 累加、消息计数与模型计数正确。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AggregatesTokensMessagesAndModels_FromRealEventFiles()
    {
        // 会话 A：快照 1000/200，模型 sonnet，2 轮对话
        await AppendSessionAsync(ModelA, new TokenUsage(1000, 200), turns: 2);
        // 会话 B：快照 500/100，模型 gpt-5，1 轮对话
        await AppendSessionAsync(ModelB, new TokenUsage(500, 100), turns: 1);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync([], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        var text = ((CommandResult.TextResult)result).Value;

        text.Should().Contain("last 2 sessions");
        // 每轮 = 1 条 user_message + 1 条 assistant_message，共 3 轮 → 6 条
        text.Should().Contain("Total messages:      6");
        text.Should().Contain("Total input tokens:  1,500");
        text.Should().Contain("Total output tokens: 300");
        text.Should().Contain("Models used:");
        text.Should().Contain(ModelA);
        text.Should().Contain(ModelB);
        // 两个模型各覆盖 1 个会话
        text.Should().Contain("1 sessions");
    }

    /// <summary>
    /// 同一会话文件可含多个快照（快照变更时追加），<c>total_usage</c> 是**累计值**——
    /// 只有序号最大的那条应被计入。按条累加会导致重复计数（回归风险）。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CountsOnlyLatestSnapshot_WhenSessionHasMultiple()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = SessionId.NewId();

        // 早期快照：300/50，模型 sonnet；后续快照：900/150，模型 gpt-5（累计值更大）
        await _store.AppendAsync(
        [
            Started(sessionId, ModelA, new TokenUsage(300, 50), now),
            Snapshot(sessionId, ModelA, new TokenUsage(300, 50), now),
            Snapshot(sessionId, ModelB, new TokenUsage(900, 150), now),
        ], TestContext.Current.CancellationToken);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync([], TestContext.Current.CancellationToken);
        var text = ((CommandResult.TextResult)result).Value;

        // 仅最新快照的累计值，而非 300+900
        text.Should().Contain("Total input tokens:  900");
        text.Should().Contain("Total output tokens: 150");
        // 仅最新快照的模型被登记
        text.Should().Contain(ModelB);
        text.Should().NotContain(ModelA);
    }

    /// <summary>事件目录为空（无任何会话）时给出明确提示，而非空报表。</summary>
    [Fact]
    public async Task ExecuteAsync_NoEventFiles_ReturnsNoSessionsMessage()
    {
        var sut = CreateSut();

        var result = await sut.ExecuteAsync([], TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        ((CommandResult.TextResult)result).Value.Should().Be("No sessions to analyze.");
    }

    /// <summary>
    /// 损坏的事件行不得静默吞掉——必须计入统计并在输出中提示数据质量，
    /// 否则用户看到的 token 总额偏低且无感知。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MalformedLine_IsCountedAndSurfacedInOutput()
    {
        await AppendSessionAsync(ModelA, new TokenUsage(700, 70), turns: 0);

        // 向真实存储写出的文件追加一行损坏 JSON
        var eventFile = Directory.GetFiles(EventDirectory, "*.jsonl").Should().ContainSingle().Subject;
        await File.AppendAllTextAsync(eventFile, "{ this is not valid json\n", TestContext.Current.CancellationToken);

        var sut = CreateSut();
        var result = await sut.ExecuteAsync([], TestContext.Current.CancellationToken);
        var text = ((CommandResult.TextResult)result).Value;

        // 有效数据仍正常统计
        text.Should().Contain("Total input tokens:  700");
        // 损坏行被计数并提示
        text.Should().Contain("Data quality:");
        text.Should().Contain("skipped 1 malformed line(s)");
    }

    // 辅助

    private const string ModelA = "claude-sonnet-4-6";
    private const string ModelB = "gpt-5";

    /// <summary>事件目录由存储端解析——测试不重复实现路径逻辑，避免契约漂移。</summary>
    private string EventDirectory =>
        Path.Combine(_home, OneCode.Infrastructure.Config.Constants.App.ConfigDirName,
            OneCode.Infrastructure.Config.Constants.Subdirs.Events);

    private InsightsCommand CreateSut() =>
        new(NullLogger<InsightsCommand>.Instance, userHomeOverride: _home);

    /// <summary>
    /// 写出**一个**会话文件：一条 SessionStarted 快照 + 若干轮对话。
    /// 每轮产生 1 条 user_message + 1 条 assistant_message（即 2 条消息事件）。
    /// 消息事件与快照同文件，保证会话计数不受辅助方法影响。
    /// </summary>
    private async Task AppendSessionAsync(string model, TokenUsage usage, int turns)
    {
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;
        var events = new List<SessionEvent> { Started(sessionId, model, usage, now) };

        for (var i = 0; i < turns; i++)
        {
            events.Add(new UserMessageEvent(sessionId, 0, now, new UserMessage($"u{i}", "hi", now)));
            events.Add(new AssistantMessageEvent(
                sessionId, 0, now, new AssistantMessage($"a{i}", [new TextBlock("ok")], now)));
        }

        await _store.AppendAsync(events, TestContext.Current.CancellationToken);
    }

    private static SessionEvent Started(SessionId id, string model, TokenUsage usage, DateTimeOffset now) =>
        new SessionStartedEvent(id, 0, now, "session", @"E:\proj", model,
            ConversationStatus.Active, usage, now, now, Branch: null);

    private static SessionEvent Snapshot(SessionId id, string model, TokenUsage usage, DateTimeOffset now) =>
        new SessionSnapshotEvent(id, 0, now, "session", @"E:\proj", model,
            ConversationStatus.Active, usage, now, now, Branch: null);
}
