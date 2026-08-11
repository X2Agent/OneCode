using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="MessageCloner"/>
/// </summary>
public sealed class MessageClonerTests
{
    [Fact]
    public void CloneMessage_UserMessage_CreatesDeepCopy()
    {
        var original = new UserMessage("1", "Test content", DateTimeOffset.Now);
        var clone = MessageCloner.CloneMessage(original);

        clone.Should().NotBeSameAs(original);
        clone.Should().BeOfType<UserMessage>();
        var userClone = (UserMessage)clone;
        userClone.Id.Should().Be(original.Id);
        userClone.Content.Should().Be(original.Content);
        userClone.Timestamp.Should().Be(original.Timestamp);
    }

    [Fact]
    public void CloneMessage_AssistantMessage_CreatesDeepCopy()
    {
        var original = new AssistantMessage(
            "1",
            new List<ContentBlock> { new TextBlock("Response") },
            DateTimeOffset.Now
        );
        var clone = MessageCloner.CloneMessage(original);

        clone.Should().NotBeSameAs(original);
        clone.Should().BeOfType<AssistantMessage>();
        var assistantClone = (AssistantMessage)clone;
        assistantClone.Id.Should().Be(original.Id);
        assistantClone.Content.Should().NotBeSameAs(original.Content);
        assistantClone.Content.Should().HaveCount(1);
    }

    [Fact]
    public void CloneMessage_AssistantWithToolUse_ClonesToolUseBlock()
    {
        var original = new AssistantMessage(
            "1",
            new List<ContentBlock>
            {
                new ToolUseBlock("tool-1", "BashTool", "{\"command\":\"ls\"}")
            },
            DateTimeOffset.Now
        );
        var clone = MessageCloner.CloneMessage(original);

        var assistantClone = (AssistantMessage)clone;
        assistantClone.Content.Should().HaveCount(1);
        assistantClone.Content[0].Should().BeOfType<ToolUseBlock>();
        var toolUseClone = (ToolUseBlock)assistantClone.Content[0];
        toolUseClone.Should().NotBeSameAs(original.Content[0]);
        toolUseClone.Id.Should().Be("tool-1");
        toolUseClone.Name.Should().Be("BashTool");
    }

    [Fact]
    public void CloneMessage_SystemMessage_CreatesDeepCopy()
    {
        var original = new SystemMessage("1", "System info", DateTimeOffset.Now);
        var clone = MessageCloner.CloneMessage(original);

        clone.Should().NotBeSameAs(original);
        clone.Should().BeOfType<SystemMessage>();
        var systemClone = (SystemMessage)clone;
        systemClone.Id.Should().Be(original.Id);
        systemClone.Content.Should().Be(original.Content);
    }

    [Fact]
    public void CloneMessage_ToolResultMessage_CreatesDeepCopy()
    {
        var original = new ToolResultMessage(
            "1", "tool-1", "BashTool", "Output", false, DateTimeOffset.Now
        );
        var clone = MessageCloner.CloneMessage(original);

        clone.Should().NotBeSameAs(original);
        clone.Should().BeOfType<ToolResultMessage>();
        var toolResultClone = (ToolResultMessage)clone;
        toolResultClone.ToolUseId.Should().Be("tool-1");
        toolResultClone.ToolName.Should().Be("BashTool");
        toolResultClone.Content.Should().Be("Output");
    }

    [Fact]
    public void CloneContentBlock_TextBlock_CreatesDeepCopy()
    {
        var original = new TextBlock("Test text");
        var clone = MessageCloner.CloneContentBlock(original);

        clone.Should().NotBeSameAs(original);
        clone.Should().BeOfType<TextBlock>();
        ((TextBlock)clone).Text.Should().Be("Test text");
    }

    [Fact]
    public void CloneContentBlock_ThinkingBlock_CreatesDeepCopy()
    {
        var original = new ThinkingBlock("Thinking process");
        var clone = MessageCloner.CloneContentBlock(original);

        clone.Should().NotBeSameAs(original);
        clone.Should().BeOfType<ThinkingBlock>();
        ((ThinkingBlock)clone).Thinking.Should().Be("Thinking process");
    }

    [Fact]
    public void CloneContentBlock_ToolUseBlock_CreatesDeepCopy()
    {
        var original = new ToolUseBlock("tool-1", "ReadTool", "{\"path\":\"file.txt\"}");
        var clone = MessageCloner.CloneContentBlock(original);

        clone.Should().NotBeSameAs(original);
        clone.Should().BeOfType<ToolUseBlock>();
        var toolUseClone = (ToolUseBlock)clone;
        toolUseClone.Id.Should().Be("tool-1");
        toolUseClone.Name.Should().Be("ReadTool");
        toolUseClone.Input.Should().Be("{\"path\":\"file.txt\"}");
    }
}
