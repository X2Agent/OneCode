using OneCode.Core.Agent;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// M3 行为契约：会话工作记忆按 profile 启用，不是全局开关。
/// </summary>
/// <remarks>
/// 只读 Agent 与并发成员都不能拿到写面：前者与只读政策冲突，后者会在并发成员间共享一个可写目录。
/// 这条边界由 profile 能力集表达，公共 opt-out 不再参与。
/// </remarks>
public sealed class FileMemoryProfilePolicyTests
{
    [Fact]
    public void Main_EnablesWorkingMemory()
    {
        PipelineProfileBehavior.For(PipelineProfile.Full)
            .Has(AgentCapability.FileMemory).Should().BeTrue(
                "the interactive Main session is the first path to get working memory");
    }

    [Theory]
    [InlineData(PipelineProfile.Worker)]
    [InlineData(PipelineProfile.TeamMember)]
    [InlineData(PipelineProfile.Explore)]
    [InlineData(PipelineProfile.Plan)]
    public void NonMainProfiles_DoNotGetWorkingMemory(PipelineProfile profile)
    {
        PipelineProfileBehavior.For(profile)
            .Has(AgentCapability.FileMemory).Should().BeFalse(
                "working memory must not be handed to read-only or concurrently-shared paths");
    }

    /// <summary>
    /// 能力集必须真的到达 Harness：只断言 profile 表而不断言 options，
    /// 会让「能力声明了但没人消费」这种回归静默通过。
    /// </summary>
    [Fact]
    public void PipelineOptions_ReflectProfileCapability()
    {
        var main = BuildOptions(PipelineProfile.Full);
        var explore = BuildOptions(PipelineProfile.Explore);

        main.EnableFileMemory.Should().BeTrue();
        explore.EnableFileMemory.Should().BeFalse();
    }

    /// <summary>
    /// 待办清单：普通 Agent 用 Harness <c>todos_*</c>，只读 Agent 不给写面。
    /// 只读 profile 的白名单会拒绝 <c>todos_*</c>，若仍声明该能力，
    /// 提示词会广告一组调用必失败的工具。
    /// </summary>
    [Fact]
    public void TodoCapability_MatchesReadOnlyBoundary()
    {
        BuildOptions(PipelineProfile.Full).EnableTodo.Should().BeTrue();
        BuildOptions(PipelineProfile.Worker).EnableTodo.Should().BeTrue();
        BuildOptions(PipelineProfile.Explore).EnableTodo.Should().BeFalse();
        BuildOptions(PipelineProfile.Plan).EnableTodo.Should().BeFalse();
    }

    /// <summary>
    /// R4：Team 成员启用框架审批协议，由 Workflow executor 拦截并桥接给宿主。
    /// 关掉它只能靠产品侧 inline 分支补位——那正是本轮要移除的双轨。
    /// </summary>
    [Fact]
    public void TeamMember_KeepsFrameworkApprovalEnabled()
    {
        PipelineProfileBehavior.For(PipelineProfile.TeamMember)
            .EnableToolApproval.Should().BeTrue(
                "the workflow intercepts approval requests, so the framework protocol can carry them");
    }

    // M2: store 根必须绑定项目，不能绑定进程 cwd

    /// <summary>
    /// 反证：Harness 默认把工作记忆根放在 <c>Directory.GetCurrentDirectory()</c>，
    /// 那是进程目录而非项目目录。用 <c>/cd</c> 或在不同工作区运行会静默读写另一棵树。
    /// </summary>
    [Fact]
    public void WorkingMemoryRoot_IsScopedToProjectNotProcessDirectory()
    {
        var projectA = Path.Combine(Path.GetTempPath(), "onecode-proj-a");
        var projectB = Path.Combine(Path.GetTempPath(), "onecode-proj-b");

        var rootA = FileMemoryStorePaths.ResolveRoot(projectA);
        var rootB = FileMemoryStorePaths.ResolveRoot(projectB);

        rootA.Should().Be(Path.Combine(projectA, ".onecode", "agent-file-memory"));
        rootA.Should().NotBe(rootB, "two projects must not share one working-memory tree");
        rootA.Should().NotStartWith(Environment.CurrentDirectory,
            "the root follows the project, not the process directory");
    }

    /// <summary>工作记忆与长期记忆必须分目录：前者是原始笔记，后者是治理后的知识。</summary>
    [Fact]
    public void WorkingMemoryRoot_IsSeparateFromLongTermMemory()
    {
        var project = Path.Combine(Path.GetTempPath(), "onecode-proj-separation");

        var working = FileMemoryStorePaths.ResolveRoot(project);
        var longTerm = Path.Combine(project, ".onecode", "memory");

        working.Should().NotBe(longTerm);
    }

    private static AgentPipelineOptions BuildOptions(PipelineProfile profile)
    {
        var ctx = new PipelineSecurityContext(
            WorkingDirectory: Environment.CurrentDirectory,
            PermissionMode: Core.Permissions.PermissionMode.Default,
            RulesBySource: null,
            AdditionalWorkingDirectories: null,
            Hook: null,
            VerificationProvider: null,
            EnableVerification: false,
            OrchestrationEventSink: null,
            FileChangeCallback: null,
            ModelId: null,
            ProviderId: null);

        return AgentPipelineOptionsFactory.Create(
            profile,
            ctx,
            new PipelineRoleOverrides(MaxToolCalls: 10, ToolLimitMessage: "limit"));
    }
}
