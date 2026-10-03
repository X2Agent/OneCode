using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// <see cref="Conversation.DisplayTitle"/> 标题推导规则守护：
/// 用户显式命名优先；否则取首条非 meta、非空用户消息的首行按字符截断；
/// 无可用内容（如尚未有消息的新会话）返回 null。
/// </summary>
public sealed class ConversationTests
{
    [Fact]
    public void DisplayTitle_ExplicitName_TakesPrecedence()
    {
        var conversation = new Conversation { Name = "my task" };
        conversation.Messages.Add(new UserMessage("u1", "hello", DateTimeOffset.UtcNow));

        conversation.DisplayTitle().Should().Be("my task");
    }

    [Fact]
    public void DisplayTitle_BlankName_FallsBackToFirstMessage()
    {
        var conversation = new Conversation { Name = "   " };
        conversation.Messages.Add(new UserMessage("u1", "hello", DateTimeOffset.UtcNow));

        conversation.DisplayTitle().Should().Be("hello");
    }

    [Fact]
    public void DisplayTitle_Unnamed_SkipsMetaAndBlankMessages_UsesFirstRealContent()
    {
        var conversation = new Conversation();
        conversation.Messages.Add(new UserMessage("u0", "meta context", DateTimeOffset.UtcNow, IsMeta: true));
        conversation.Messages.Add(new UserMessage("u1", "  \n ", DateTimeOffset.UtcNow));
        conversation.Messages.Add(new UserMessage("u2", "帮我修复登录超时", DateTimeOffset.UtcNow));

        conversation.DisplayTitle().Should().Be("帮我修复登录超时");
    }

    [Fact]
    public void DisplayTitle_MultiLineMessage_UsesFirstLine()
    {
        var conversation = new Conversation();
        conversation.Messages.Add(new UserMessage("u1", "第一行标题\r\n第二行正文", DateTimeOffset.UtcNow));

        conversation.DisplayTitle().Should().Be("第一行标题");
    }

    [Fact]
    public void DisplayTitle_ExceedsMaxChars_TruncatesWithEllipsis()
    {
        var conversation = new Conversation();
        conversation.Messages.Add(new UserMessage("u1", new string('a', 50), DateTimeOffset.UtcNow));

        var title = conversation.DisplayTitle(40);

        title.Should().Be(new string('a', 40) + "…");
        title!.Length.Should().Be(41);
    }

    [Fact]
    public void DisplayTitle_NoUsableMessages_ReturnsNull()
    {
        var conversation = new Conversation();
        conversation.Messages.Add(new UserMessage("u0", "meta only", DateTimeOffset.UtcNow, IsMeta: true));

        conversation.DisplayTitle().Should().BeNull();
    }
}
