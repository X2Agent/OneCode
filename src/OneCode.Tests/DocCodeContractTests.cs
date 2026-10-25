using System.Text.RegularExpressions;
using OneCode.Core.Config;

namespace OneCode.Tests;

/// <summary>
/// 文档-代码行为契约守卫：补 <see cref="DocFactConsistencyTests"/> 未覆盖的盲区。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DocFactConsistencyTests"/> 只守路径/幽灵 API/计数与压缩比例；机制名、默认值语义、
/// ADR 决策是否落地没有守卫。历史后果：ADR 0010 把 GOAL 子目标的审批路径写成「pending 上浮」，
/// 而代码早已改为 unattended + 错误回喂；MAF 指南引用了一个从不存在的 <c>harnessOwnsToolApproval</c>。
/// </para>
/// <para>
/// 这里刻意使用「源码/文档文本断言」而非行为测试：被守的是<b>声明与实现的一致性</b>——
/// 行为测试无法证明文档写了什么，而这类漂移正是本组测试要防的。
/// </para>
/// </remarks>
[Trait("Category", "Docs")]
public sealed class DocCodeContractTests
{
    private const string GuideFile = "docs/maf/integration-guide.md";
    private const string Adr0007File = "docs/adr/0007-maf-integration-boundaries.md";
    private const string Adr0011File = "docs/adr/0011-background-agents-delegation-boundary.md";
    private const string SettingsDocFile = "docs/settings.md";
    private const string SkillsDocFile = "docs/skills.md";
    private const string SubAgentsDocFile = "docs/sub-agents.md";
    private const string CompactDocFile = "docs/compact-thresholds.md";

    private const string CronExecutorFile = "src/OneCode.App/Services/Cron/CronJobExecutor.cs";
    private const string PipelineBuilderFile = "src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs";
    private const string ForkedRunnerFile = "src/OneCode.App/Services/Agent/ForkedAgentRunner.cs";
    private const string MainRunnerFile = "src/OneCode.App/Services/Agent/MainAgentRunner.Pipeline.cs";
    private const string AutoDreamPromptFile = "src/OneCode.App/prompts/system/autodream-consolidation.prompt";
    private const string CompactionBuilderFile = "src/OneCode.Infrastructure/Agent/CompactionPipelineBuilder.cs";

    // ---------------------------------------------------------------- 幽灵符号

    /// <summary>审批归属不是某个布尔字段；这个符号在代码中从未存在过。</summary>
    [Fact]
    public void Docs_DoNotReferenceGhostApprovalOwnershipSymbol()
    {
        const string ghost = "harnessOwnsToolApproval";

        foreach (var doc in new[] { GuideFile, Adr0007File })
        {
            Read(doc).Should().NotContain(ghost,
                $"{doc} 引用了代码中不存在的符号 '{ghost}'；审批只由 EnableToolApproval 开关控制");
        }
    }

    // ---------------------------------------------------------------- 配置默认值与语义

    /// <summary>
    /// settings.md 的配置项表必须与 <see cref="SettingDescriptors"/> 的键集一致。
    /// </summary>
    /// <remarks>
    /// 只匹配「键 | 类型 | ...」形态的行，排除权限模式表（首列是模式名）与代码映射表（第二列是 ConfigKeys.*）。
    /// </remarks>
    [Fact]
    public void SettingsDoc_ConfigKeysExistInDescriptors()
    {
        var row = new Regex(
            @"^\| `([A-Za-z][\w.]*)` \| `(?:string|string\[\]|bool|int|long|double)` \|",
            RegexOptions.Multiline);

        var keys = row.Matches(Read(SettingsDocFile))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        keys.Should().NotBeEmpty("settings.md 的配置项表必须可解析；若表格形态变了，请同步本测试");

        var unknown = keys.Where(k => !SettingDescriptors.TryGet(k, out _)).ToList();
        unknown.Should().BeEmpty(
            "settings.md 列出了代码不认识的配置项（会被 ConfigManager 当未知键忽略）："
            + string.Join(", ", unknown));
    }

    /// <summary>
    /// <c>hasTrustAccepted</c> 是全局信任开关，不是「当前项目已信任」的记录。
    /// </summary>
    [Fact]
    public void SettingsDoc_TrustFlagIsDescribedAsGlobal()
    {
        var doc = Read(SettingsDocFile);

        doc.Should().Contain("全局信任开关", "hasTrustAccepted=true 会信任一切目录，文档不得弱化为单项目语义");
        doc.Should().NotContain("是否已接受当前项目根目录信任", "该描述会让读者误以为它只影响当前项目");
    }

    // ---------------------------------------------------------------- ADR 决策落地

    /// <summary>Cron 以 Build + unattended 运行：既不使用 Goal，也不丢弃事件。</summary>
    [Fact]
    public void Code_CronRunsBuildUnattendedAndConsumesErrors()
    {
        var code = Read(CronExecutorFile);

        code.Should().Contain("WorkingMode.Build", "无人值守 cron 的只读语义来自 unattended，不是 WorkingMode.Goal");
        code.Should().NotContain("WorkingMode.Goal", "Goal 会进入持久化 AwaitingApproval，不适合无人值守");
        code.Should().Contain("ErrorEvent", "cron 必须消费错误事件才能判定失败并通知");
    }

    /// <summary>审批标记只在装配期施加一次；配额超限走错误返回而非补标/Ask。</summary>
    [Fact]
    public void Code_ToolApprovalMarkerAppliedExactlyOnce()
    {
        var code = Read(PipelineBuilderFile);

        Regex.Matches(code, @"ToolApprovalMarker\.Apply\(").Count.Should().Be(1,
            "审批标记只有一次装配期施加；provider 注入工具由请求期 ToolApprovalMarkingContextProvider 补标");
    }

    /// <summary>迭代上限在设置 MaxTurns 时必须是 MaxTurns + 1，保留一轮收尾。</summary>
    [Fact]
    public void Code_IterationLimitReservesWrapUpTurn()
        => Read(PipelineBuilderFile).Should().Contain("maxTurns + 1");

    // ---------------------------------------------------------------- 生命周期

    /// <summary>主代理与 fork 两条路径都必须持有 per-run context provider 的租约。</summary>
    [Fact]
    public void Code_BothRunnerPathsTrackContextProviderLease()
    {
        Read(MainRunnerFile).Should().Contain("AgentContextProviderLease.Track(contextProviders)");
        Read(ForkedRunnerFile).Should().Contain("AgentContextProviderLease.Track(contextProviders)",
            "fork 不经过主代理管道；不在这里 Track 会让每 run 新建的 skills provider 泄漏");
    }

    /// <summary>AutoDream 提示词必须引用真实存在的记忆分区标题。</summary>
    [Fact]
    public void Prompt_AutoDreamReferencesExistingMemorySection()
    {
        var prompt = Read(AutoDreamPromptFile);

        prompt.Should().Contain("### User memories", "MemoryService 实际输出的手动条目分区标题");
        prompt.Should().NotContain("### Manual entries", "该分区名不存在，软约束会失效");
    }

    // ---------------------------------------------------------------- 交叉文档一致性

    /// <summary>子代理文档必须说明两条路径都释放 provider。</summary>
    [Fact]
    public void SubAgentsDoc_DescribesBothLeasePaths()
    {
        var doc = Read(SubAgentsDocFile);

        doc.Should().Contain("AgentContextProviderLease");
        doc.Should().Contain("两条路径");
    }

    /// <summary>技能发现是「规则同构」，不得再声称两侧共用同一实现。</summary>
    [Fact]
    public void SkillsDoc_DoesNotClaimSharedDiscoveryImplementation()
    {
        var doc = Read(SkillsDocFile);

        doc.Should().Contain("同构");
        doc.Should().NotContain("共用 `SkillDiscovery`");
    }

    /// <summary>F2 已部分落地：sink 接通、文件回调未接线，状态不得再写「待立项」。</summary>
    [Fact]
    public void BackgroundAgentsAdr_F2StatusReflectsPartialImplementation()
    {
        var doc = Read(Adr0011File);

        doc.Should().Contain("部分完成");
        doc.Should().Contain("FileChangeCallback");
    }

    /// <summary>只读研究子代理的绝对上限必须同时出现在文档与 builder 常量中。</summary>
    [Fact]
    public void CompactDoc_ResearchCapsMatchBuilderConstants()
    {
        var doc = Read(CompactDocFile);
        var code = Read(CompactionBuilderFile);

        foreach (var cap in new[] { "40,000", "80,000", "120,000" })
            doc.Should().Contain(cap, $"只读研究档的绝对上限 {cap} 是文档承诺的一部分");

        code.Should().Contain("40_000").And.Contain("80_000").And.Contain("120_000");
    }

    // ---------------------------------------------------------------- 基础设施

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>从测试二进制目录向上定位仓库根（含 src/OneCode.slnx）。</summary>
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "OneCode.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"无法从 {AppContext.BaseDirectory} 向上定位仓库根。");
    }
}
