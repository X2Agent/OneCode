using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// 模式提示词 ↔ 生产工具注册表交叉校验：提示词是模型可见的「施工图」，
/// 里面点名的工具必须在生产注册表里真实存在，且派工语义不得指向宿主后台任务工具。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这组守卫</b>：提示词是模型逐轮可见的施工图。<c>Task</c> 现为宿主后台任务检查/控制
/// （get/list/stop/output），不再是派工入口——真正的派工工具是 <c>Agent</c>。弱模型照提示词调错工具，
/// 正好命中「未知工具兜底」自愈链路也多绕一轮。这与本仓库的病灶同源：幽灵引用（不存在的类名/路径/API）
/// 会在文档间长期滞留。
/// </para>
/// <para>
/// <b>真实写入端</b>：① <c>AddToolServices()</c> 的注册表（与生产同一份代码路径，
/// 做法同 <see cref="ProductionApprovalBoundaryTests"/>——只取注册描述符，不解析 DI 图）；
/// ② 仓库内的 <c>src/OneCode.App/prompts/system/*.prompt</c> 文件。提示词是被验证的读取端。
/// </para>
/// </remarks>
public sealed class ModePromptToolReferenceTests
{
    /// <summary>
    /// 主代理模式管线实际加载的提示词（<c>MainModeContextProviderBuilder</c> /
    /// <c>BuildModeAttachmentProvider</c> / <c>PlanModeAttachmentProvider</c> /
    /// <c>GoalSubGoalExecutor</c> / Team orchestrator 角色的 <c>system/coordinator</c>）。
    /// 只有这组会被主模型逐轮看到，工具名引用必须与注册表一致。
    /// </summary>
    private static readonly string[] ModePrompts =
    [
        "build", "plan", "team", "goal",
        "goal-subgoal", "default", "coordinator",
    ];

    /// <summary>
    /// 派工工具集合——与 <see cref="ToolCapabilitySet.IsAllowed"/> 中 <c>AllowSubAgents</c>
    /// 门禁的判定集合同源（该处硬编码 <c>toolName is "Agent" or "ParallelAgents"</c>）。
    /// </summary>
    private static readonly string[] DelegationTools = ["Agent", "ParallelAgents"];

    /// <summary>
    /// 宿主后台任务工具——<c>Task</c> 自改为宿主任务检查/控制后不再是派工入口，
    /// <c>BackgroundRun</c>/<c>BackgroundWait</c> 只负责命令的后台执行与等待。
    /// </summary>
    private static readonly string[] HostBackgroundTaskTools = ["Task", "BackgroundRun", "BackgroundWait"];

    /// <summary>行内工具引用形态一：反引号包裹的单个标识符（如 <c>`Glob`</c>）。</summary>
    private static readonly Regex BacktickedToolName = new(@"`([A-Z][A-Za-z0-9]*)`", RegexOptions.Compiled);

    /// <summary>行内工具引用形态二：「X tool」短语（如 <c>Bash tool</c>），首字母大写以排除 "by tool" 之类行文。</summary>
    private static readonly Regex ToolPhrase = new(@"\b([A-Z][A-Za-z0-9]+) tool\b", RegexOptions.Compiled);

    /// <summary>派工语义行关键词——出现即表示该行在讲「把工作交给子代理」。</summary>
    private static readonly Regex DelegationKeyword = new("sub-agent|delegat|parallelize|spawn", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 模式提示词里点名的每个工具都必须在生产注册表中存在。
    /// </summary>
    /// <remarks>
    /// 反证：把任一模式提示词里的真实工具名改成不存在的名字（如 <c>Read tool</c> → <c>Readd tool</c>），
    /// 本用例必须失败——否则它只是个恒真断言。
    /// </remarks>
    [Fact]
    public void ModePrompts_OnlyReferenceRegisteredTools()
    {
        var registered = ProductionToolNames();
        var violations = new List<string>();

        foreach (var prompt in ModePrompts)
        {
            foreach (var line in File.ReadAllLines(PromptPath(prompt)))
            {
                foreach (var name in ExtractToolReferences(line))
                {
                    if (!registered.Contains(name))
                    {
                        violations.Add($"{prompt}.prompt 引用了未注册的工具 '{name}'");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "提示词点名的工具必须在 AddToolServices 注册表中存在；"
            + "若工具已改名/删除，同步改提示词（历史同类病灶：幽灵引用长期滞留）");
    }

    /// <summary>
    /// 讲派生的行不得点名宿主后台任务工具——派工入口只有 <c>Agent</c>/<c>ParallelAgents</c>。
    /// </summary>
    /// <remarks>
    /// 反证即本守卫的立项现场：<c>build.prompt</c> 的「sub-agents (Task tool) could parallelize」
    /// 让本用例失败（Task 是宿主后台任务工具，不是派工工具）；改为 Agent 后转绿。
    /// </remarks>
    [Fact]
    public void DelegationGuidance_NeverNamesHostBackgroundTaskTools()
    {
        var registered = ProductionToolNames();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(SystemPromptDirectory, "*.prompt"))
        {
            var prompt = Path.GetFileNameWithoutExtension(file);
            foreach (var line in File.ReadAllLines(file))
            {
                if (!DelegationKeyword.IsMatch(line))
                {
                    continue;
                }

                foreach (var name in ExtractToolReferences(line).Concat(KnownToolNamesIn(line, registered)))
                {
                    if (HostBackgroundTaskTools.Contains(name) && !DelegationTools.Contains(name))
                    {
                        violations.Add(
                            $"{prompt}.prompt 在派工语义行点名了宿主后台任务工具 '{name}'：{line.Trim()}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "子代理派工入口只有 Agent / ParallelAgents；Task / BackgroundRun / BackgroundWait "
            + "是宿主后台任务面，写进派工指引会把模型引去调错工具");
    }

    /// <summary>
    /// 提取一行内的工具名引用：反引号标识符 + 「X tool」短语，仅保留首字母大写的候选。
    /// </summary>
    private static IEnumerable<string> ExtractToolReferences(string line)
    {
        foreach (Match match in BacktickedToolName.Matches(line))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in ToolPhrase.Matches(line))
        {
            yield return match.Groups[1].Value;
        }
    }

    /// <summary>
    /// 行内裸写的已知工具名（如 <c>Agent, ParallelAgents (sub-agent delegation tools)</c>）——
    /// 只匹配生产注册表里的名字，避免把行文里的普通大写词误判为工具。
    /// </summary>
    private static IEnumerable<string> KnownToolNamesIn(string line, HashSet<string> registered)
        => registered.Where(name => Regex.IsMatch(line, $@"\b{Regex.Escape(name)}\b"));

    /// <summary>
    /// 与生产同一份注册表（<c>AddToolServices</c>），只取注册描述符中的工具名，
    /// 不解析 DI 图——工具构造依赖会拉起整张服务图，而本测试只关心「注册了什么名字」。
    /// </summary>
    private static HashSet<string> ProductionToolNames()
    {
        var services = new ServiceCollection();
        OneCode.App.Tools.ToolServiceCollectionExtensions.AddToolServices(services);

        var names = services
            .Where(d => d.ServiceType == typeof(ToolRegistration))
            .Select(d => (ToolRegistration)d.ImplementationInstance!)
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        names.Should().NotBeEmpty("AddToolServices must contribute tool registrations");
        return names;
    }

    private static string SystemPromptDirectory =>
        Path.Combine(FindRepoRoot(), "src", "OneCode.App", "prompts", "system");

    private static string PromptPath(string name) =>
        Path.Combine(SystemPromptDirectory, name + ".prompt");

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
