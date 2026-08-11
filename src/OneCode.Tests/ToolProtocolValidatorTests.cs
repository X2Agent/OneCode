using Microsoft.Extensions.AI;
using OneCode.App.Services.Compact;

namespace OneCode.Tests;

public sealed class ToolProtocolValidatorTests
{
    private readonly ToolProtocolValidator _sut = new();

    [Fact]
    public void Validate_CompleteParallelBatch_IsValid()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "Read", null),
                new FunctionCallContent("call-2", "Glob", null),
            ]),
            new(ChatRole.User, [new FunctionResultContent("call-1", "one")]),
            new(ChatRole.User, [new FunctionResultContent("call-2", "two")]),
            new(ChatRole.Assistant, "done"),
        };

        _sut.Validate(messages).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_MissingResult_ReportsMissingResult()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "Read", null),
                new FunctionCallContent("call-2", "Glob", null),
            ]),
            new(ChatRole.User, [new FunctionResultContent("call-1", "one")]),
        };

        var result = _sut.Validate(messages);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.Code == ToolProtocolErrorCode.MissingResult && error.CallId == "call-2");
    }

    [Fact]
    public void Validate_TextInterruptsOpenBatch_ReportsBatchInterrupted()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "Read", null)]),
            new(ChatRole.Assistant, "illegal interruption"),
            new(ChatRole.User, [new FunctionResultContent("call-1", "late")]),
        };

        var result = _sut.Validate(messages);

        result.Errors.Should().Contain(error => error.Code == ToolProtocolErrorCode.BatchInterrupted);
    }

    [Fact]
    public void Validate_DuplicateAndOrphanResults_AreRejected()
    {
        var messages = new ChatMessage[]
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "Read", null)]),
            new(ChatRole.User,
            [
                new FunctionResultContent("call-1", "one"),
                new FunctionResultContent("call-1", "duplicate"),
                new FunctionResultContent("orphan", "orphan"),
            ]),
        };

        var result = _sut.Validate(messages);

        result.Errors.Should().Contain(error => error.Code == ToolProtocolErrorCode.DuplicateResult);
        result.Errors.Should().Contain(error => error.Code == ToolProtocolErrorCode.OrphanResult);
    }
}
