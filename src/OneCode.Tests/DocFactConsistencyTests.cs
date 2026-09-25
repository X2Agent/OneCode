using System.Text.RegularExpressions;
using OneCode.Core.Hooks;
using OneCode.Core.Memory;
using OneCode.Core.Permissions;

namespace OneCode.Tests;

/// <summary>
/// 文档事实守卫：锁死「文档声明 ↔ 代码真相源」的一致性，防止幽灵 API、幽灵路径与计数漂移。
/// </summary>
/// <remarks>
/// <para>
/// <b>为何需要这组测试</b>：本仓库只有 <c>scripts/check-docs.ps1</c> 校验了 <c>docs/commands.md</c> 的命令清单，
/// 文档里的 <b>API 名 / 路径名 / 计数</b>从未被任何自动化覆盖。历史后果：
/// </para>
/// <list type="bullet">
/// <item>一个不存在的 API <c>AddTool&lt;T&gt;</c> 曾污染 5 处文档（含根 <c>AGENTS.md</c>）</item>
/// <item>已解散的 <c>ServiceCollectionExtensions.Business.cs</c> 在 6 处文档中被当作真实路径引用</item>
/// <item>工具数、权限模式数、钩子事件数长期漂移且相互矛盾</item>
/// </list>
/// <para>
/// <b>三类守卫</b>：① 计数一致（与枚举/注册清单比对）；② 幽灵路径（文档引用的注册类必须真实存在）；
/// ③ 幽灵 API（已废弃的 API 名不得再出现在操作性文档中）。
/// </para>
/// <para>
/// 规则见 <c>src/OneCode.Tests/AGENTS.md</c>「契约测试：让写入路径与读取路径互相验证」——
/// 本测试的"真实写入端"是代码侧真相源（枚举 / 注册清单 / 文件系统），文档是被验证的读取端。
/// </para>
/// <para>
/// <b>新增文档时若本测试失败</b>：先核对代码是否为真相源，再决定改文档还是改代码——
/// 不要为了让测试变绿而放宽断言。
/// </para>
/// </remarks>
public sealed class DocFactConsistencyTests
{
    // 权威真相源文件（相对仓库根）
    private const string ToolRegistrationFile = "src/OneCode.App/Tools/ToolServiceCollectionExtensions.cs";
    private const string AutomationRegistrationFile = "src/OneCode.Automation/ServiceCollectionExtensions.cs";
    private const string ReadmeCnFile = "README_CN.md";
    private const string ReadmeFile = "README.md";
    private const string RootAgentsFile = "AGENTS.md";
    private const string AppAgentsFile = "src/OneCode.App/AGENTS.md";
    private const string HooksDocFile = "docs/hooks.md";
    private const string CompactThresholdsDocFile = "docs/compact-thresholds.md";
    private const string CompactionBuilderFile = "src/OneCode.Infrastructure/Agent/CompactionPipelineBuilder.cs";

    // ---------------------------------------------------------------- 幽灵 API 守卫

    /// <summary>
    /// 已废弃/从未存在的 API 名不得出现在文档里——它们是"施工图上的假地址"。
    /// </summary>
    /// <remarks>
    /// 判据：这些名字在全仓库的 <b>注册代码</b>中零命中（唯一入口是 <c>AddToolInstance&lt;T&gt;</c>）。
    /// 若未来真的引入同名 API，应同步把该名字从本清单移除并新增真实守卫。
    /// </remarks>
    [Theory]
    [InlineData("AddToolStatic")]
    [InlineData("RegisterHookSubsystem")]
    [InlineData("RegisterMemoryServices")]
    [InlineData("LambdaHookExecutor")]
    [InlineData("SnipDuplicateCallsCompactionStrategy")]
    [InlineData("CompactionProviderBuilder")]
    public void Docs_DoNotReferenceNonExistentApis(string ghostApi)
    {
        foreach (var doc in OperativeDocs())
        {
            var text = File.ReadAllText(RepoPath(doc));
            text.Should().NotContain(ghostApi,
                $"{doc} 引用了不存在的 API '{ghostApi}'——请改为真实入口（见 Core/Tools/ToolRegistrationExtensions.cs）");
        }
    }
    /// <summary>
    /// 文件名不得冒充类型名：文件 <c>RetryOnOverloadMiddleware.cs</c> 的真实类型是
    /// <c>RetryOnOverloadChatClient</c>。文档以裸 token 引用前者会被读者当作类型使用，
    /// 因此除非后跟 <c>.cs</c>（即明确谈文件），否则不得出现。
    /// </summary>
    /// <remarks>
    /// 判据：反引号包裹的 `RetryOnOverloadMiddleware` 且不紧跟 <c>.cs</c> 即违规。
    /// 言及文件名的合法写法：`RetryOnOverloadMiddleware.cs`（后跟 <c>.cs</c>，本测试放行）。
    /// </remarks>
    [Fact]
    public void Docs_DoNotUseFileNameAsTypeName()
    {
        var fileNameAsType = new Regex(@"`RetryOnOverloadMiddleware`(?!\\.cs)", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var doc in AllDocsWithPathReferences())
        {
            var text = File.ReadAllText(RepoPath(doc));
            foreach (Match match in fileNameAsType.Matches(text))
            {
                var line = text[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{doc}:{line} 引用了不存在的类型「RetryOnOverloadMiddleware」——真实类型是 RetryOnOverloadChatClient，文件名是 RetryOnOverloadMiddleware.cs");
            }
        }

        violations.Should().BeEmpty(
            "文件名不得冒充类型名；幽灵类名会长期滞留并误导后续改动。"
            + (violations.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, violations) : ""));
    }


    /// <summary>
    /// <c>AddTool&lt;T&gt;</c> 是幽灵 API（唯一入口是 <c>AddToolInstance&lt;T&gt;</c>）。
    /// 用正则区分二者，避免把合法的 <c>AddToolInstance</c> 误判。
    /// </summary>
    [Fact]
    public void Docs_DoNotUseGhostAddToolGenericApi()
    {
        // AddTool 后紧跟 `<` 或 `(`（排除 AddToolInstance / AddToolServices / AddToolDone）
        var ghost = new Regex(@"\bAddTool(?!Instance|Services|Done)\s*[<(]", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var doc in OperativeDocs())
        {
            // 反证发现：单行可能同时含正确写法与错误写法，必须校验全部匹配而非首个。
            foreach (Match match in ghost.Matches(File.ReadAllText(RepoPath(doc))))
            {
                violations.Add($"{doc} 出现幽灵 API '{match.Value}'");
            }
        }

        violations.Should().BeEmpty(
            "工具注册唯一入口是 AddToolInstance<T>；AddTool<T> / AddToolStatic 从未存在过（历史漂移源头）");
    }

    // ---------------------------------------------------------------- 幽灵路径守卫

    /// <summary>
    /// 文档中引用的 <c>XxxServiceCollectionExtensions.cs</c> 路径必须真实存在。
    /// </summary>
    /// <remarks>
    /// 「已解散的注册类」是本仓库最顽固的幽灵路径来源：按启动批次分桶的
    /// <c>ServiceCollectionExtensions.*.cs</c> partial 类已全部解散为领域自有注册类
    /// （由 <see cref="RegistrationOwnershipTests"/> 守卫禁止复活），但文档引用长期滞留。
    /// </remarks>
    [Fact]
    public void Docs_ServiceCollectionExtensionPathsExist()
    {
        // 负向断言同上：避免把 `XxxServiceCollectionExtensions.csproj` 的前缀当成路径
        var pathPattern = new Regex(@"[\w./\\-]*ServiceCollectionExtensions(?:\.\w+)?\.cs(?![\w])", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var doc in AllDocsWithPathReferences())
        {
            var fullPath = RepoPath(doc);
            var lines = File.ReadAllLines(fullPath);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in pathPattern.Matches(lines[i]))
                {
                    var referenced = match.Value;

                    // 占位符示例名（如 XxxServiceCollectionExtensions.cs）不指向真实文件
                    if (IsPlaceholderPath(referenced))
                    {
                        continue;
                    }

                    if (ExistsAsSourceFile(referenced))
                    {
                        continue;
                    }

                    // 历史引用豁免：须有限定词 + 同行给出真实新位置（见 IsHistoricalReference）
                    if (IsHistoricalReference(lines[i], referenced))
                    {
                        continue;
                    }

                    violations.Add($"{doc}:{i + 1} 引用不存在的注册类「{referenced}」");
                }
            }
        }

        violations.Should().BeEmpty(
            "文档引用的注册类必须真实存在；如需记录历史路径，须写限定词（原/已下沉/已解散/历史/旧）"
            + "并同时给出一个真实存在的新路径。");
    }

    // ---------------------------------------------------------------- 计数守卫

    /// <summary>
    /// 文档中引用的 <c>.cs</c> 文件必须真实存在（不限 <c>*ServiceCollectionExtensions*</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为何需要这条更宽的守卫</b>：原守卫的正则只匹配 <c>*ServiceCollectionExtensions*.cs</c>，
    /// 其余 <c>.cs</c> 引用完全不检查。历史后果：已删除的 <c>TaskContextProvider.cs</c> 与
    /// 已迁移的 <c>App/Services/Memory/MemoryEntryStore.cs</c> 长期留在文档里，
    /// 而 <c>SkillChangeWatcher.cs</c> 这个<b>从未存在过</b>的类名（真实类为
    /// <c>SkillFilesWatcher</c>）也通过了全部检查。
    /// </para>
    /// <para>
    /// <b>排除项</b>：
    /// <list type="bullet">
    /// <item>MAF 源码引用（见 <see cref="IsMafSourceReference"/>）——本地只读 checkout；存在时一并校验，未 checkout 时跳过</item>
    /// <item>占位符示例名（<c>Xxx*</c> / <c>My*</c> 等，见 <see cref="IsPlaceholderPath"/>）</item>
    /// <item>历史引用（限定词 + 同行给出真实新位置，或删除限定词，见 <see cref="IsHistoricalReference(string, string?, string)"/>）</item>
    /// <item>被省略的路径片段（如 <c>.cs</c>、<c>ServiceCollectionExtensions.*.cs</c> 通配）</item>
    /// </list>
    /// </para>
    /// </remarks>
    [Fact]
    public void Docs_AllReferencedSourceFilesExist()
    {
        // 负向断言：`.cs` 后不得再接单词字符，否则会把 `OneCode.Cli.csproj` 的前缀当成路径。
        // 不能排除 `)`/`]` 等后续字符——多数引用写在 Markdown 链接 `[标签](路径)` 里。
        var pathPattern = new Regex(@"[\w./\\-]*\.cs(?![\w])", RegexOptions.Compiled);
        var violations = new List<string>();
        var mafAvailable = MafSourceRoot() is not null;

        foreach (var doc in AllDocsWithPathReferences())
        {
            var lines = File.ReadAllLines(RepoPath(doc));

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in pathPattern.Matches(lines[i]))
                {
                    var referenced = match.Value;

                    if (!IsCheckablePath(referenced)
                        || IsGlobFragment(lines[i], match)
                        || ExistsAsSourceFile(referenced)
                        || IsHistoricalReference(lines[i], EnclosingTableHeader(lines, i), referenced))
                    {
                        continue;
                    }

                    // MAF 源码是本地只读 checkout：存在时上面已连同 src/ 一并校验；
                    // 未 checkout 时无法对框架侧引用取证，跳过而非报假违规。
                    if (!mafAvailable && IsMafSourceReference(referenced))
                    {
                        continue;
                    }

                    violations.Add($"{doc}:{i + 1} 引用不存在的源文件「{referenced}」");
                }
            }
        }

        violations.Should().BeEmpty(
            "文档引用的源文件必须真实存在；幽灵类名与失效路径会长期滞留并误导后续改动。"
            + "如需记录历史路径，须写限定词（原/已下沉/已解散/历史/旧/不再）并同时给出真实新路径。"
            + (violations.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, violations) : ""));
    }

    /// <summary>
    /// MAF 源码引用的路径前缀——文档记录 MAF 上游（pinned <c>dotnet-1.22.0</c>）取证时的惯用写法。
    /// </summary>
    private static readonly string[] MafSourcePathPrefixes =
    [
        "agent-framework/",     // 仓库相对：agent-framework/dotnet/src/...
        "dotnet/",              // 简写：dotnet/src/Microsoft.Agents.AI/...
        "Microsoft.Agents",     // 包相对：Microsoft.Agents.AI/Harness/...
        "Microsoft.Extensions.AI",
        "Harness/",             // 包内相对：Harness/ToolApproval/...
    ];

    /// <summary>
    /// MAF 上游源码的裸文件名——只可能指向 MAF checkout，不可能存在于本仓库。
    /// 本地有 MAF checkout 时这些名字仍随 <see cref="VerifiableSourceFiles"/> 逐一校验
    /// （不存在即违规）；未 checkout 时无法取证，由 <see cref="Docs_AllReferencedSourceFilesExist"/>
    /// 跳过。新增 MAF 引用若忘记补录，CI 会失败提醒——这是有意识的动作，不是静默放行。
    /// </summary>
    private static readonly HashSet<string> MafSourceFileNames = new(StringComparer.Ordinal)
    {
        "AIAgentChatClient.cs",
        "AIAgentStructuredOutput.cs",
        "AIJudgeLoopEvaluator.cs",
        "AgentExtensions.cs",
        "ApprovalRequirement.cs",
        "BackgroundAgentsProvider.cs",
        "BackgroundTaskCompletionLoopEvaluator.cs",
        "ChatClientExtensions.cs",
        "ChatClientHarnessExtensions.cs",
        "CompletionMarkerLoopEvaluator.cs",
        "FeatureIndex.cs",
        "HarnessAgent.cs",
        "HarnessAgentOptions.cs",
        "HostedWorkflowState.cs",
        "LoopAgent.cs",
        "LoopAgentOptions.cs",
        "LoopContext.cs",
        "OpenTelemetryAgent.cs",
        "SummarizationCompactionStrategy.cs",
    };

    /// <summary>是否为 MAF 源码引用（只读 checkout，可能未拉取）。</summary>
    private static bool IsMafSourceReference(string referenced)
    {
        var normalized = referenced.Replace('\\', '/');
        if (MafSourcePathPrefixes.Any(p => normalized.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }

        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return MafSourceFileNames.Contains(fileName);
    }

    /// <summary>
    /// 是否为 glob 通配的一部分（如 <c>*Tests.cs</c>、<c>Xxx*.cs</c>）——通配片段不是具体路径。
    /// </summary>
    private static bool IsGlobFragment(string line, Match match)
        => (match.Index > 0 && line[match.Index - 1] == '*')
        || (match.Index + match.Length < line.Length && line[match.Index + match.Length] == '*');

    /// <summary>
    /// 是否值得校验：排除通配片段、占位符与省略前缀。
    /// </summary>
    private static bool IsCheckablePath(string referenced)
    {
        // 通配符片段（`ServiceCollectionExtensions.*.cs`、`OneCodeToplevel*.cs`）不是具体路径
        if (referenced.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        // 占位符示例名
        if (IsPlaceholderPath(referenced))
        {
            return false;
        }

        // 省略前缀的片段（如 `.cs`、`ChatClientHarnessExtensions.cs` 之外的裸后缀）
        var fileName = Path.GetFileName(referenced.Replace('\\', '/'));
        return fileName.Length > 3
            && fileName.Contains('.')
            && !fileName.StartsWith('.');
    }

    // ---------------------------------------------------------------- 计数守卫

    /// <summary>
    /// 工具数 = <c>AddToolInstance</c> 调用数 + <c>AddCronTools</c> 贡献的工具数（1）。
    /// </summary>
    [Fact]
    public void Docs_ToolCountMatchesRegistrationSource()
    {
        var count = CountRegisteredTools();

        AssertDeclaredCount(ReadmeCnFile, @"共 (\d+) 个工具", count, "工具总数");
        AssertDeclaredCount(RootAgentsFile, @"(\d+) 个工具", count, "工具总数");
        AssertDeclaredCount(AppAgentsFile, @"Agent 调用的 (\d+) 个工具", count, "工具总数");
        AssertDeclaredCount(ReadmeFile, @"(\d+) built-in tools", count, "工具总数");
    }

    /// <summary>
    /// 权限模式数 = <c>PermissionMode</c> 枚举成员数。文档曾长期写 9 种并包含一个不存在的 <c>Bubble</c>。
    /// </summary>
    [Fact]
    public void Docs_PermissionModeCountMatchesEnum()
    {
        var count = Enum.GetValues<PermissionMode>().Length;

        AssertDeclaredCount(ReadmeCnFile, @"(\d+) 种 `PermissionMode`", count, "权限模式数");
        AssertDeclaredCount(ReadmeFile, @"(\d+) permission modes", count, "权限模式数");
        AssertDeclaredCount(ReadmeCnFile, @"权限系统（(\d+) 种模式", count, "权限模式数");
    }

    /// <summary>
    /// 开放拦截点数 = <c>HookInterceptionPoints.Open</c> 数量。
    /// </summary>
    [Fact]
    public void Docs_HookInterceptionPointCountMatchesOpenPoints()
    {
        var count = HookInterceptionPoints.Open.Count;

        AssertDeclaredCount(HooksDocFile, @"(\d+) 种拦截点", count, "拦截点数");
    }

    /// <summary>
    /// 记忆作用域数 = <c>MemoryScope</c> 枚举成员数。文档曾写「三级作用域」并包含已删除的 Session 级。
    /// </summary>
    [Fact]
    public void Docs_MemoryScopeIsTwoLevel()
    {
        var count = Enum.GetValues<MemoryScope>().Length;
        count.Should().Be(2, "MemoryScope 只有 User / Project 两级；会话记忆子系统已物理删除");

        var readme = File.ReadAllText(RepoPath(ReadmeCnFile));
        readme.Should().Contain($"{CountToChinese(count)}级作用域",
            "README_CN 的记忆作用域描述须与 MemoryScope 枚举一致");
        readme.Should().NotContain("Session（当前会话）",
            "会话记忆子系统已删除，不应再作为记忆作用域出现");
    }

    /// <summary>
    /// 压缩阈值文档声明的比例必须等于 <c>CompactionPipelineBuilder</c> 的常量，且不得重新引入被移除的钳制。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/compact-thresholds.md</c> 是阈值唯一对外说明；比例一旦漂移，读者会按错误的触发点判断
    /// 「为什么没有压缩」。真相源是 builder 的常量，文档是被验证的读取端。
    /// </para>
    /// <para>
    /// 反证部分锁死修复前的形态：<c>inputBudget = max(1, window - output)</c>。该钳制会把非法模型配置
    /// 变成永不触发的阈值，文档不得再以任何形式描述它。
    /// </para>
    /// </remarks>
    [Fact]
    public void Docs_CompactThresholdRatiosMatchBuilderConstants()
    {
        var builder = File.ReadAllText(RepoPath(CompactionBuilderFile));
        var doc = File.ReadAllText(RepoPath(CompactThresholdsDocFile));

        var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(builder, @"const double (\w+Ratio) = ([\d.]+);"))
        {
            ratios[match.Groups[1].Value] = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        ratios.Should().NotBeEmpty("阈值常量是真相源；解析不到说明常量被重命名，须同步本测试");

        var expectations = new (string Agent, string Prefix)[]
        {
            ("Main", "Main"),
            ("Worker", "Worker"),
        };

        foreach (var (agent, prefix) in expectations)
        {
            var row = Regex.Match(
                doc,
                $@"^\|\s*`BuildFor{agent}Agent`\s*\|[^|]*\|([^|]*)\|([^|]*)\|([^|]*)\|",
                RegexOptions.Multiline);
            row.Success.Should().BeTrue($"{CompactThresholdsDocFile} 必须列出 BuildFor{agent}Agent 的阈值行");

            var declared = row.Groups.Cast<Group>().Skip(1)
                .Select(g => g.Value.Trim())
                .ToArray();
            var expected = new[]
            {
                ratios[$"{prefix}ToolEvictionRatio"],
                ratios[$"{prefix}SummarizationRatio"],
                ratios[$"{prefix}TruncationRatio"],
            }.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)).ToArray();

            declared.Should().Equal(expected,
                $"文档阈值行必须与 {prefix}*Ratio 常量一致（顺序：折叠 / 摘要 / 截断）");
        }

        var overhead = ratios["RequestOverheadRatio"].ToString("0.00", CultureInfo.InvariantCulture);
        doc.Should().Contain($"RequestOverheadRatio = {overhead}",
            "预算必须扣除请求开销预留，文档须给出与代码一致的比例");
        doc.Should().NotContain("max(1,",
            "被移除的钳制会让非法配置退化成永不触发的阈值，文档不得再描述该公式");
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>操作性文档——其中的 API 名与路径是"施工图"，必须与代码一致。</summary>
    private static IReadOnlyList<string> OperativeDocs() =>
    [
        RootAgentsFile,
        "src/AGENTS.md",
        AppAgentsFile,
        ReadmeFile,
        ReadmeCnFile,
        CompactThresholdsDocFile,
    ];

    /// <summary>含路径引用的文档——含 docs/、ADR 与子代理/模式文档（ADR 的扩展示例同样是施工图）。</summary>
    private static IReadOnlyList<string> AllDocsWithPathReferences() =>
    [
        .. OperativeDocs(),
        "docs/adr/0003-m4-approval-event-streaming.md",
        "docs/adr/0004-memory-module-design.md",
        "docs/adr/0005-hook-module-design.md",
        "docs/adr/0007-maf-integration-boundaries.md",
        "docs/adr/0001-permission-vs-toolapproval-vs-filter.md",
        "docs/adr/0002-declarative-workflow-assessment.md",
        "docs/adr/0006-query-stream-state-object.md",
        "docs/adr/0008-structured-output-text-fallback.md",
        "docs/adr/0009-background-responses-assessment.md",
        "docs/adr/0010-agent-loop-boundaries.md",
        "docs/adr/0011-background-agents-delegation-boundary.md",
        "docs/sub-agents.md",
        "docs/team-modes.md",
        "docs/background-services.md",
        "docs/compact-thresholds.md",
        "docs/hooks.md",
        "docs/memory-overview.md",
        "docs/settings.md",
        "docs/skills.md",
        "docs/commands.md",
        "docs/maf/integration-guide.md",
    ];

    /// <summary>限定词——出现这些词说明该行是历史叙述，而非当前路径指引。</summary>
    private static readonly string[] HistoricalQualifiers = ["原", "已下沉", "已解散", "历史", "旧", "不再", "曾经的", "改名", "更名", "重命名"];

    /// <summary>
    /// 删除限定词——类型/文件已被删除时没有「新位置」可指，不适用 <see cref="IsHistoricalReference(string, string?, string)"/> 的
    /// 「限定词 + 真实新位置」双重条件。
    /// </summary>
    private static readonly string[] DeletionQualifiers = ["已删除", "已移除", "已废弃", "已取消"];

    /// <summary>
    /// 占位符文件名前缀——文档惯用的「示例命名」（如 <c>XxxServiceCollectionExtensions.cs</c>、<c>MyTool.cs</c>），
    /// 不对应真实文件，不算幽灵路径。
    /// </summary>
    private static readonly string[] PlaceholderPrefixes = ["Xxx", "Foo", "Bar", "Your", "My", "Some", "Example"];

    /// <summary>
    /// 是否为占位符/命名模板。<c>Xxx</c> 是本仓库约定的占位 token，允许出现在文件名任意位置
    /// （命名规范表的左列形如 <c>IXxx.cs</c>、<c>XxxService.cs</c>——它们描述模式，不是路径引用）。
    /// </summary>
    private static bool IsPlaceholderPath(string referenced)
    {
        var fileName = Path.GetFileName(referenced.Replace('\\', '/'));

        if (fileName.Contains("Xxx", StringComparison.Ordinal))
        {
            return true;
        }

        return PlaceholderPrefixes.Any(p => fileName.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// 判定是否为「历史引用」：行内含限定词 <b>且</b> 同行还给出一个真实存在的新位置。
    /// 只写限定词而不给新位置的行仍算违规——否则"已解散"会变成逃避校验的口令。
    /// </summary>
    /// <remarks>
    /// 新位置有两种合法写法：
    /// <list type="bullet">
    /// <item>真实存在的 <c>.cs</c> 路径（如 <c>src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs</c>）</item>
    /// <item>命名空间全限定类型名（如 <c>OneCode.Infrastructure.Ai.ChatClientFactory</c>）——
    /// 其末段必须确实是 src 下声明的类型（见 <see cref="DeclaredTypeNames"/>）</item>
    /// </list>
    /// </remarks>
    private static bool IsHistoricalReference(string line, string referenced)
        => IsHistoricalReference(line, enclosingTableHeader: null, referenced);

    /// <summary>
    /// 判定是否为「历史引用」。<paramref name="enclosingTableHeader"/> 为所在表格的表头行——
    /// 表头写「原位置」时，整张表的旧路径都由表头承担限定词，无需逐行重复。
    /// </summary>
    private static bool IsHistoricalReference(string line, string? enclosingTableHeader, string referenced)
    {
        var context = enclosingTableHeader is null ? line : line + "\n" + enclosingTableHeader;

        // 删除记录：文件已不存在，文档记录的正是「它没了」——不要求给出新位置
        if (DeletionQualifiers.Any(q => context.Contains(q, StringComparison.Ordinal)))
        {
            return true;
        }

        if (!HistoricalQualifiers.Any(q => context.Contains(q, StringComparison.Ordinal)))
        {
            return false;
        }

        var pathPattern = new Regex(@"[\w./\\-]*\.cs(?![\w])", RegexOptions.Compiled);
        if (pathPattern.Matches(context)
            .Select(m => m.Value)
            .Any(p => !string.Equals(p, referenced, StringComparison.Ordinal) && ExistsAsSourceFile(p)))
        {
            return true;
        }

        // 全限定类型名（至少两段点分，首字母大写）末段须是真实声明的类型。
        var typePattern = new Regex(@"\b(?:[A-Z]\w+\.)+([A-Z]\w+)\b", RegexOptions.Compiled);
        return typePattern.Matches(context)
            .Select(m => m.Groups[1].Value)
            .Any(name => DeclaredTypeNames().Contains(name));
    }

    /// <summary>
    /// 返回 <paramref name="index"/> 所在 Markdown 表格的表头行；不在表格中则返回 null。
    /// </summary>
    /// <remarks>
    /// 表格里「原位置 / 新位置」这类列语义由表头一次性声明。只看单行会让每行都必须重复限定词，
    /// 于是校验被迫放宽或文档被迫啰嗦——两者都不好。
    /// </remarks>
    private static string? EnclosingTableHeader(string[] lines, int index)
    {
        // 表格以 `|---|` 分隔行紧跟表头；向上找到最近的分隔行，其上一行即表头。
        for (var i = index - 1; i >= 0 && i >= index - 40; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (!trimmed.StartsWith('|'))
            {
                return null;
            }

            if (IsTableSeparator(trimmed) && i > 0)
            {
                return lines[i - 1];
            }
        }

        return null;
    }

    private static bool IsTableSeparator(string trimmed)
        => trimmed.Replace("|", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(":", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Length == 0;

    private static HashSet<string>? _declaredTypeNames;

    /// <summary>src 下声明的类型名集合（class / record / interface / enum / struct）。</summary>
    private static HashSet<string> DeclaredTypeNames()
    {
        if (_declaredTypeNames is not null)
        {
            return _declaredTypeNames;
        }

        var pattern = new Regex(
            @"\b(?:class|record|interface|enum|struct)\s+([A-Z]\w+)",
            RegexOptions.Compiled);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(FindRepoRoot()))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        _declaredTypeNames = names;
        return names;
    }

    /// <summary>
    /// 判断文档中的路径片段是否对应真实源文件。支持四种写法：
    /// 仓库相对路径（<c>src/...</c> 或 <c>agent-framework/...</c>）、项目内部分路径（<c>Query/Foo.cs</c>）、仅文件名（<c>Foo.cs</c>）。
    /// </summary>
    /// <remarks>
    /// 「项目内部分路径」是常见写法（如在 <c>src/AGENTS.md</c> 中引用 <c>Query/ChatClientServiceCollectionExtensions.cs</c>，
    /// 省略了 <c>src/OneCode.App/</c> 前缀）——不能按仓库根拼接，需按路径后缀匹配。
    /// </remarks>
    private static bool ExistsAsSourceFile(string referenced)
    {
        var repoRoot = FindRepoRoot();
        var normalized = referenced.Replace('\\', '/').TrimStart('.', '/');

        if (normalized.StartsWith("src/", StringComparison.Ordinal)
            || normalized.StartsWith("agent-framework/", StringComparison.Ordinal))
        {
            return File.Exists(Path.Combine(repoRoot, normalized));
        }

        // 部分路径或纯文件名：按「路径后缀」匹配（排除 obj/bin）
        var suffix = Path.DirectorySeparatorChar + normalized.Replace('/', Path.DirectorySeparatorChar);
        return VerifiableSourceFiles(repoRoot)
            .Any(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SourceFiles(string repoRoot) =>
        EnumerateSourceFiles(Path.Combine(repoRoot, "src"));

    /// <summary>
    /// 可校验的源文件集合：本仓库 <c>src/</c> + 本地 MAF checkout（若存在）。
    /// </summary>
    /// <remarks>
    /// 声明类型名只应扫本仓库 <c>src/</c>（见 <see cref="DeclaredTypeNames"/>）——把 MAF 类型算进来
    /// 会让历史豁免误接受框架类型名。此处只用于「文件是否存在」的判断。
    /// </remarks>
    private static IEnumerable<string> VerifiableSourceFiles(string repoRoot)
    {
        var roots = new List<string> { Path.Combine(repoRoot, "src") };
        if (MafSourceRoot() is { } mafRoot)
        {
            roots.Add(mafRoot);
        }

        return roots.SelectMany(EnumerateSourceFiles);
    }

    /// <summary>
    /// 本地 MAF 只读 checkout 的源码根；未 checkout（或 checkout 不完整）时返回 null。
    /// </summary>
    /// <remarks>
    /// 必须按 <c>dotnet/src</c> 而非 <c>agent-framework</c> 判断——仓库索引里提交了
    /// <c>agent-framework</c> 的 gitlink（无 <c>.gitmodules</c>），CI checkout 会留下
    /// <b>空目录</b>；按父目录判断会把「无源码可校验」误判为「可校验」，
    /// 导致整批 MAF 上游引用报假违规（本地能过只因开发者有完整 checkout）。
    /// </remarks>
    private static string? MafSourceRoot()
    {
        var mafRoot = Path.Combine(FindRepoRoot(), "agent-framework", "dotnet", "src");
        return Directory.Exists(mafRoot) ? mafRoot : null;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            : [];

    /// <summary>统计注册的工具数：<c>AddToolInstance</c> 调用数 + Cron 工具数。</summary>
    private static int CountRegisteredTools()
    {
        var toolRegistrations = Regex.Matches(
            File.ReadAllText(RepoPath(ToolRegistrationFile)),
            @"^\s*services\.AddToolInstance(?:<[^>]+>)?\(",
            RegexOptions.Multiline).Count;

        var cronRegistrations = Regex.Matches(
            File.ReadAllText(RepoPath(AutomationRegistrationFile)),
            @"^\s*services\.AddToolInstance(?:<[^>]+>)?\(",
            RegexOptions.Multiline).Count;

        return toolRegistrations + cronRegistrations;
    }

    /// <summary>断言文档中的数字声明与代码真相值一致。</summary>
    private static void AssertDeclaredCount(string doc, string pattern, int expected, string what)
    {
        var match = Regex.Match(File.ReadAllText(RepoPath(doc)), pattern);
        match.Success.Should().BeTrue(
            $"{doc} 应包含形如「{pattern}」的{what}声明——若表述已改，请同步更新本测试的正则");

        var declared = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        declared.Should().Be(expected,
            $"{doc} 声明{what}为 {declared}，但代码真相源为 {expected}");
    }

    private static string CountToChinese(int count) => count switch
    {
        2 => "两",
        3 => "三",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    private static string RepoPath(string relativePath) =>
        Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>从测试二进制目录向上定位仓库根（含 src/OneCode.slnx）。</summary>
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "OneCode.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"无法从 {AppContext.BaseDirectory} 向上定位仓库根。");
    }
}
