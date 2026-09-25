using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Session;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// 多轮历史读取经 MAF <see cref="ChatHistoryProvider"/> 契约桥接到会话转录。
/// </summary>
public sealed class TranscriptChatHistoryProviderTests
{
    private static readonly SessionId ConversationId = SessionId.NewId();

    [Fact]
    public async Task InvokingAsync_ExcludesLatestUserMessageAlreadyInTranscript()
    {
        var reader = Substitute.For<ISessionChatHistoryReader>();
        reader.GetChatHistory(ConversationId).Returns(
        [
            new ChatMessage(ChatRole.User, "earlier question"),
            new ChatMessage(ChatRole.Assistant, "earlier answer"),
            new ChatMessage(ChatRole.User, "current question"),
        ]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "current question")))).ToList();

        result.Should().HaveCount(3, "transcript history minus the latest user turn, plus the request message");
        result[0].Text.Should().Be("earlier question");
        result[1].Text.Should().Be("earlier answer");
        result[2].Text.Should().Be("current question");
    }

    [Fact]
    public async Task InvokingAsync_KeepsTranscriptWhenLatestUserDoesNotMatch()
    {
        var reader = Substitute.For<ISessionChatHistoryReader>();
        reader.GetChatHistory(ConversationId).Returns(
        [
            new ChatMessage(ChatRole.User, "earlier question"),
            new ChatMessage(ChatRole.Assistant, "earlier answer"),
        ]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "different question")))).ToList();

        result.Should().HaveCount(3);
        result[2].Text.Should().Be("different question");
    }

    [Fact]
    public async Task InvokingAsync_EmptyTranscript_ReturnsOnlyRequestMessages()
    {
        var reader = Substitute.For<ISessionChatHistoryReader>();
        reader.GetChatHistory(ConversationId).Returns([]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "first question")))).ToList();

        result.Should().ContainSingle().Which.Text.Should().Be("first question");
    }

    private static ChatHistoryProvider.InvokingContext CreateContext(params ChatMessage[] requestMessages)
        => new(Substitute.For<AIAgent>(), session: null, requestMessages);
}
