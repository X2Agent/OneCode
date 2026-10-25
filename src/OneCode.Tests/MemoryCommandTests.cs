using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Memory;

namespace OneCode.Tests;

/// <summary>
/// 验证 <see cref="MemoryCommand"/> 的语义化 Key 生成与 <c>/memory add</c> 落盘契约。
/// </summary>
public sealed class MemoryCommandTests
{
    [Fact]
    public void BuildManualKey_GeneratesSemanticKebabSlugWithShortTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 10, 14, 21, 30, 0, TimeSpan.Zero);

        var key = MemoryCommand.BuildManualKey("Use dotnet build src/OneCode.slnx to compile", timestamp);

        key.Should().Be("manual:use-dotnet-build-src-10142130");
    }

    [Fact]
    public async Task ExecuteAsync_AddProjectMemory_SavesWithSemanticKeyPrefixAndNeverExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(new Conversation());
        var entryStore = Substitute.For<IMemoryEntryStore>();
        var sut = new MemoryCommand(
            sessionManager,
            Substitute.For<IMemoryService>(),
            entryStore,
            autoDreamService: null!);

        var result = await sut.ExecuteAsync(["add", "Always", "run", "dotnet", "test"], ct);

        var textResult = result.Should().BeOfType<CommandResult.TextResult>().Subject;
        textResult.Value.Should().Contain("Added MEMORY.md entry [project] (manual:always-run-dotnet-test-");
        await entryStore.Received(1).UpsertAsync(
            MemoryScope.Project,
            Arg.Is<IReadOnlyList<MemoryEntry>>(entries =>
                entries.Count == 1 &&
                entries[0].Key.StartsWith("manual:always-run-dotnet-test-", StringComparison.Ordinal) &&
                entries[0].Value == "Always run dotnet test" &&
                entries[0].Source == "manual" &&
                entries[0].ExpiresAt == null),
            ct);
        await entryStore.DidNotReceive().UpsertAsync(
            MemoryScope.User,
            Arg.Any<IReadOnlyList<MemoryEntry>>(),
            Arg.Any<CancellationToken>());
    }
}
