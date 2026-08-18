using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.App.Services.AutoDream;
using OneCode.App.Services.Memory;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// /memory list 回归测试（B11）：session memories 仅展示（不带可删除编号），
/// /memory remove 明确只针对 persistent entries，避免与 persistent 编号混淆误删。
/// </summary>
public sealed class MemoryCommandTests
{
    private static AutoDreamService CreateAutoDream(string tempDir)
    {
        var agent = new AutoDreamAgentDependencies(
            Substitute.For<IChatClient>(),
            Substitute.For<IToolCatalog>(),
            Substitute.For<IModelManager>(),
            Substitute.For<IPromptManager>());
        var storage = new AutoDreamStorageDependencies(
            Substitute.For<IMemoryEntryStore>(),
            Substitute.For<IConfigManager>(),
            Substitute.For<IWorkingDirectoryAccessor>());
        return new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent,
            storage,
            globalConfigDirOverride: tempDir);
    }

    [Fact]
    public async Task List_SessionMemories_HaveNoNumberedRemoveTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = new Conversation { WorkingDirectory = Path.GetTempPath() };

        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(conv);

        var sessionMemoryService = Substitute.For<ISessionMemoryService>();
        sessionMemoryService.GetMemories(conv)
            .Returns(new[]
            {
                new SessionMemoryEntry("id1", "Session fact one", "auto", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            });

        var memoryService = Substitute.For<IMemoryService>();
        memoryService.ListMemoryEntriesAsync(conv.WorkingDirectory, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemoryEntryInfo>());

        var sut = new MemoryCommand(
            sessionManager,
            memoryService,
            Substitute.For<IMemoryEntryStore>(),
            sessionMemoryService,
            CreateAutoDream(Path.Combine(Path.GetTempPath(), $"autodream-{Guid.NewGuid():N}")));

        var result = await sut.ExecuteAsync(["list"], ct);
        var text = result.Should().BeOfType<CommandResult.TextResult>().Subject.Value;

        // session 条目内容保留展示，但不再带可删除编号
        text.Should().Contain("Session memories");
        text.Should().Contain("• [auto] Session fact one");
        text.Should().NotContain("1. [auto]");

        // Usage 明确 remove 只针对 persistent entries
        text.Should().Contain("not removable");
        text.Should().Contain("Remove persistent entry");
    }
}