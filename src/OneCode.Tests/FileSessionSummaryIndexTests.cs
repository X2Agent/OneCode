using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Session;
using OneCode.Core.Session;
using OneCode.Infrastructure.Persistence;

namespace OneCode.Tests;

/// <summary>
/// 会话摘要索引的失败边界：索引只是加速层——写入失败或被取消时，
/// 已落盘的会话保存必须照常完成，且不得留下临时文件。
/// </summary>
public sealed class FileSessionSummaryIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-summary-index-" + Guid.NewGuid().ToString("N"));

    public FileSessionSummaryIndexTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WriteAsync_WithCanceledToken_DoesNotThrowAndLeavesNoTempFile()
    {
        var index = CreateIndex();
        var summary = CreateSummary();
        var stamp = new SessionEventLogStamp(summary.Id, 42, DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var write = () => index.WriteAsync(stamp, summary, cancellation.Token);

        await write.Should().NotThrowAsync("取消只该丢弃索引，不得击穿调用方的保存路径");
        File.Exists(IndexFile(summary.Id)).Should().BeFalse("取消后不得留下半写状态的索引");
        TemporaryFiles().Should().BeEmpty("取消后必须清理已创建的临时文件");
    }

    [Fact]
    public async Task SaveAsync_WhenIndexDirectoryUnavailable_StillPersistsConversation()
    {
        var events = new FileSessionEventStore(_root);
        var store = new EventSourcedSessionStore(events, CreateIndex(), NullLogger<EventSourcedSessionStore>.Instance);
        var conversation = new Conversation
        {
            Id = SessionId.NewId(),
            Name = "index-unavailable",
            WorkingDirectory = _root,
            Model = "test-model",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
        conversation.Messages.Add(new UserMessage("user", "hello", DateTimeOffset.UtcNow));

        // 索引目录在运行期消失（用户清理 ~/.onecode、权限变化）：保存必须照常完成。
        Directory.Delete(IndexDirectory(), recursive: true);

        await store.SaveAsync(conversation, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
        loaded.Should().NotBeNull("索引写入失败不得中断会话保存");
        loaded!.Messages.Should().ContainSingle().Which.Should().BeOfType<UserMessage>()
            .Which.Content.Should().Be("hello");
    }

    private FileSessionSummaryIndex CreateIndex() =>
        new(_root, NullLogger<FileSessionSummaryIndex>.Instance);

    private static ConversationSummary CreateSummary()
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationSummary(
            SessionId.NewId(), "index-only", "test-model", 1, now, now, new TokenUsage(1, 2), "build");
    }

    private string IndexDirectory() => Path.Combine(
        _root,
        OneCode.Infrastructure.Config.Constants.App.ConfigDirName,
        OneCode.Infrastructure.Config.Constants.Subdirs.Events,
        OneCode.Infrastructure.Config.Constants.Subdirs.SessionSummaries);

    private string IndexFile(SessionId sessionId) => Path.Combine(IndexDirectory(), $"{sessionId.Value}.json");

    private IReadOnlyList<string> TemporaryFiles() =>
        Directory.GetFiles(_root, "*.tmp-*", SearchOption.AllDirectories);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
