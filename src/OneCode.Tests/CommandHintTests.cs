using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Observability;
using OneCode.App.Services.Setup;
using OneCode.App.Session;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// 断言命令 ArgumentHint 元数据与真实参数面一致，防回退。
/// </summary>
public sealed class CommandHintTests
{
    [Fact]
    public void CompactCommand_Hint_DeclaresRangeFlagsAndInstructions()
    {
        // /compact 实参支持 --from/--up-to 与自定义指令（CompactCommand.cs）。
        var sut = new CompactCommand(CreateCompactService());
        sut.ArgumentHint.Should().Be("[--from <index>] [--up-to <index>] [instructions]");
    }

    [Fact]
    public void UpgradeCommand_Hint_ApplyFlagsOnly()
    {
        // /upgrade 实参只解析 --apply/-y/--yes；--check/-c 未解析（默认即检查）。
        var http = Substitute.For<IHttpClientFactory>();
        var notes = new ReleaseNotesService(http, NullLogger<ReleaseNotesService>.Instance);
        var sut = new UpgradeCommand(notes,
            new UpgradeService(http, notes, Substitute.For<IProcessRunner>(), NullLogger<UpgradeService>.Instance));

        sut.ArgumentHint.Should().Contain("--apply");
        sut.ArgumentHint.Should().Contain("-y");
        sut.ArgumentHint.Should().Contain("--yes");
        sut.ArgumentHint.Should().NotContain("--check");
        sut.ArgumentHint.Should().NotContain("-c");
    }

    [Fact]
    public void FastModelCommand_Hint_IncludesOffAndNone()
    {
        // /fastmodel 实参支持 off 与 none 两种清除写法（FastModelCommand.cs）。
        var sut = new FastModelCommand(Substitute.For<OneCode.Infrastructure.Config.IConfigManager>());
        sut.ArgumentHint.Should().Be("[<id>|off|none]");
    }

    private static CompactService CreateCompactService()
    {
        var sessionManager = new SessionManager(
            Substitute.For<ISessionStore>(),
            NullLogger<SessionManager>.Instance,
            Environment.CurrentDirectory,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            shellExecutorCleanup: Substitute.For<OneCode.App.Tools.IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new OneCode.Core.Domain.SessionIdHolder(),
            sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());

        return new CompactService(
            Substitute.For<IChatClient>(),
            NullLogger<CompactService>.Instance,
            Substitute.For<IHookExecutionService>(),
            new CompactSessionDependencies(
                sessionManager,
                sessionManager,
                Substitute.For<IModelManager>(),
                new OneCode.Infrastructure.TokenEstimator()),
            new CompactPromptBuilder(new PromptManager()),
            new CompactApplier());
    }
}