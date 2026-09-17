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
        var pathPattern = new Regex(@"[\w./\\-]*ServiceCollectionExtensions(?:\.\w+)?\.cs", RegexOptions.Compiled);
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
    /// 钩子事件数 = <c>HookEvent</c> 枚举成员数。
    /// </summary>
    [Fact]
    public void Docs_HookEventCountMatchesEnum()
    {
        var count = Enum.GetValues<HookEvent>().Length;

        AssertDeclaredCount(HooksDocFile, @"(\d+) 种生命周期事件", count, "钩子事件数");
        AssertDeclaredCount(HooksDocFile, @"HookEvent` \| (\d+) 种生命周期事件枚举", count, "钩子事件数");
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

    // ---------------------------------------------------------------- 辅助

    /// <summary>操作性文档——其中的 API 名与路径是"施工图"，必须与代码一致。</summary>
    private static IReadOnlyList<string> OperativeDocs() =>
    [
        RootAgentsFile,
        "src/AGENTS.md",
        AppAgentsFile,
        ReadmeFile,
        ReadmeCnFile,
    ];

    /// <summary>含路径引用的文档——含 docs/ 与 ADR（ADR 的扩展示例同样是施工图）。</summary>
    private static IReadOnlyList<string> AllDocsWithPathReferences() =>
    [
        .. OperativeDocs(),
        "docs/adr/0003-m4-approval-event-streaming.md",
        "docs/adr/0004-memory-module-design.md",
        "docs/adr/0005-hook-module-design.md",
        "docs/adr/0007-maf-integration-boundaries.md",
        "docs/background-services.md",
        "docs/hooks.md",
        "docs/memory-overview.md",
        "docs/settings.md",
        "docs/skills.md",
        "docs/commands.md",
        "docs/plan/harness-defaults-replacement-audit.md",
    ];

    /// <summary>限定词——出现这些词说明该行是历史叙述，而非当前路径指引。</summary>
    private static readonly string[] HistoricalQualifiers = ["原", "已下沉", "已解散", "历史", "旧", "不再", "曾经的"];

    /// <summary>
    /// 占位符文件名前缀——文档惯用的「示例命名」（如 <c>XxxServiceCollectionExtensions.cs</c>、<c>MyTool.cs</c>），
    /// 不对应真实文件，不算幽灵路径。
    /// </summary>
    private static readonly string[] PlaceholderPrefixes = ["Xxx", "Foo", "Bar", "Your", "My", "Some", "Example"];

    private static bool IsPlaceholderPath(string referenced)
    {
        var fileName = Path.GetFileName(referenced.Replace('\\', '/'));
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
    {
        if (!HistoricalQualifiers.Any(q => line.Contains(q, StringComparison.Ordinal)))
        {
            return false;
        }

        var pathPattern = new Regex(@"[\w./\\-]*\.cs", RegexOptions.Compiled);
        if (pathPattern.Matches(line)
            .Select(m => m.Value)
            .Any(p => !string.Equals(p, referenced, StringComparison.Ordinal) && ExistsAsSourceFile(p)))
        {
            return true;
        }

        // 全限定类型名（至少两段点分，首字母大写）末段须是真实声明的类型。
        var typePattern = new Regex(@"\b(?:[A-Z]\w+\.)+([A-Z]\w+)\b", RegexOptions.Compiled);
        return typePattern.Matches(line)
            .Select(m => m.Groups[1].Value)
            .Any(name => DeclaredTypeNames().Contains(name));
    }

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
    /// 判断文档中的路径片段是否对应真实源文件。支持三种写法：
    /// 仓库相对路径（<c>src/...</c>）、项目内部分路径（<c>Query/Foo.cs</c>）、仅文件名（<c>Foo.cs</c>）。
    /// </summary>
    /// <remarks>
    /// 「项目内部分路径」是常见写法（如在 <c>src/AGENTS.md</c> 中引用 <c>Query/ChatClientServiceCollectionExtensions.cs</c>，
    /// 省略了 <c>src/OneCode.App/</c> 前缀）——不能按仓库根拼接，需在 <c>src</c> 下按后缀匹配。
    /// </remarks>
    private static bool ExistsAsSourceFile(string referenced)
    {
        var repoRoot = FindRepoRoot();
        var normalized = referenced.Replace('\\', '/').TrimStart('.', '/');

        if (normalized.StartsWith("src/", StringComparison.Ordinal))
        {
            return File.Exists(Path.Combine(repoRoot, normalized));
        }

        // 部分路径或纯文件名：在 src 下按「路径后缀」匹配（排除 obj/bin）
        var suffix = Path.DirectorySeparatorChar + normalized.Replace('/', Path.DirectorySeparatorChar);
        return SourceFiles(repoRoot)
            .Any(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SourceFiles(string repoRoot) =>
        Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

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
