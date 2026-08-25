namespace OneCode.App.Tui;

/// <summary>侧边栏任务行。</summary>
public sealed record TeamSidebarTask(
    string TaskId, string Title, string Role, string Status, int? Completed, int? Total);

/// <summary>侧边栏文件变更行（按文件聚合）。</summary>
public sealed record TeamSidebarFile(
    string FileName, int Added, int Removed, string Contributors);

/// <summary>侧边栏质量门禁行。</summary>
public sealed record TeamSidebarGate(string Name, string Status);

/// <summary>澄清决策记录（每次澄清交互一条：问题摘要 → 用户回答摘要）。</summary>
public sealed record TeamSidebarDecision(string Answer);

/// <summary>
/// Team 侧边栏的只读内容投影——由 <see cref="TeamRunSnapshot"/> 生成，
/// 交由 <see cref="ChatBlockRenderers.RenderTeamSidebar"/> 渲染为 FormattedLine。
/// </summary>
public sealed record TeamSidebarContent(
    string TeamName,
    string Phase,
    IReadOnlyList<TeamSidebarTask> Tasks,
    IReadOnlyList<TeamSidebarDecision> Decisions,
    IReadOnlyList<TeamSidebarFile> Files,
    IReadOnlyList<TeamSidebarGate> Gates,
    IReadOnlyList<string> Milestones);

/// <summary>
/// 一次 Team 运行的侧边栏状态机：消费 Team 相关 <see cref="TuiEvent"/> 流，
/// 维护任务/决策/文件变更/门禁/关键节点五区数据，投影为
/// <see cref="TeamSidebarContent"/>。
///
/// 原则：只记录已确定的事实——进行中的澄清交互不进快照（属于左侧向导），
/// 回答完成后才沉淀为决策记录；文件变更仅统计 TEAM 成员（AgentName 非空）的修改。
/// 运行终态（交付/回滚）后快照冻结，新一次澄清/审批事件触发整体重置。
/// </summary>
public sealed class TeamRunSnapshot
{
    private const int MaxMilestones = 12;
    private const int MaxDecisions = 10;
    private const int MaxDecisionTextLength = 48;

    private readonly Dictionary<string, (string Title, string Role, string Status, int? Completed, int? Total)> _tasks = [];
    private readonly List<TeamSidebarDecision> _decisions = [];
    private readonly Dictionary<string, (int Added, int Removed, List<string> Agents)> _files = new(StringComparer.Ordinal);
    private readonly List<string> _fileOrder = [];
    private readonly Dictionary<string, string> _gates = new(StringComparer.Ordinal);
    private readonly List<string> _milestones = [];

    public string TeamName { get; private set; } = "";
    public string Phase { get; private set; } = "准备中";
    public bool IsTerminal { get; private set; }

    /// <summary>重置快照（新一次 Team 运行开始）。</summary>
    public void Reset()
    {
        _tasks.Clear();
        _decisions.Clear();
        _files.Clear();
        _fileOrder.Clear();
        _gates.Clear();
        _milestones.Clear();
        TeamName = "";
        Phase = "准备中";
        IsTerminal = false;
    }

    /// <summary>消费一个 TUI 事件，更新快照。非 Team 事件为 no-op。</summary>
    public void Apply(TuiEvent evt)
    {
        switch (evt)
        {
            case TuiTeamProgress { TeamName: { Length: > 0 } teamName }:
                if (IsTerminal) Reset();
                TeamName = teamName;
                Phase = "等待澄清";
                break;

            case TuiTeamUserResponse { TeamName: var team, Response: var response }:
                if (IsTerminal) Reset();
                if (TeamName.Length == 0) TeamName = team;
                if (!string.IsNullOrWhiteSpace(response))
                {
                    _decisions.Add(new TeamSidebarDecision(Truncate(response.Trim(), MaxDecisionTextLength)));
                    if (_decisions.Count > MaxDecisions)
                        _decisions.RemoveAt(0);
                }
                Phase = "规划中";
                AddMilestone("澄清完成");
                break;

            case TuiTeamPlanApproval approval:
                if (IsTerminal) Reset();
                TeamName = approval.TeamName;
                foreach (var title in approval.Tasks)
                    _tasks.TryAdd(title, (title, "", "pending", null, null));
                foreach (var gate in approval.RequiredGates)
                    _gates.TryAdd(gate, "Pending");
                Phase = "等待审批";
                break;

            case TuiAgentCoordination:
                if (Phase is "准备中" or "协调中")
                    Phase = "协调中";
                break;

            case TuiTeamTaskProgress progress:
                ApplyTaskProgress(progress);
                break;

            case TuiFileChange { AgentName: { } agent } fileChange when agent.Length > 0:
                ApplyFileChange(fileChange);
                break;

            case TuiTeamDelivery { Report: var report }:
                Phase = report.Committed ? "已完成" : "已回滚";
                foreach (var gate in report.Gates)
                    _gates[gate.GateId] = gate.Status.ToString();
                AddMilestone(report.Committed
                    ? $"交付已提交（{report.Changes.Files.Count} 个文件）"
                    : "文件修改已回滚");
                IsTerminal = true;
                break;
        }
    }

    private void ApplyTaskProgress(TuiTeamTaskProgress progress)
    {
        if (IsTerminal) return;
        if (Phase != "执行中")
        {
            if (Phase == "等待审批")
                AddMilestone("计划已批准，开始执行");
            Phase = "执行中";
        }

        var status = progress.Status switch
        {
            null => "running",
            "Succeeded" => "done",
            "Failed" or "Cancelled" => "failed",
            _ => "pending",
        };
        _tasks[progress.TaskId] = (progress.TaskTitle, progress.AssigneeRole, status, progress.CompletedTasks, progress.TotalTasks);
    }

    private void ApplyFileChange(TuiFileChange fileChange)
    {
        if (IsTerminal) return;
        var fileName = fileChange.FileName;
        if (!_files.TryGetValue(fileName, out var entry))
        {
            entry = (0, 0, []);
            _fileOrder.Add(fileName);
        }
        entry.Added += fileChange.AddedLines.Count;
        entry.Removed += fileChange.RemovedLines.Count;
        if (fileChange.AgentName is { } agent && agent.Length > 0 && !entry.Agents.Contains(agent, StringComparer.Ordinal))
            entry.Agents.Add(agent);
        _files[fileName] = entry;
    }

    private void AddMilestone(string milestone)
    {
        _milestones.Add(milestone);
        if (_milestones.Count > MaxMilestones)
            _milestones.RemoveAt(0);
    }

    /// <summary>投影为只读内容（任务/文件按出现顺序，里程碑按时间顺序）。</summary>
    public TeamSidebarContent ToContent()
    {
        var tasks = _tasks.Select(kv =>
        {
            var (title, role, status, completed, total) = kv.Value;
            return new TeamSidebarTask(kv.Key, title, role, status, completed, total);
        }).ToList();

        var files = _fileOrder.Select(name =>
        {
            var (added, removed, agents) = _files[name];
            var contributors = agents.Count == 0 ? "" : agents.Count == 1 ? agents[0] : $"{agents[0]}+{agents.Count - 1}";
            return new TeamSidebarFile(name, added, removed, contributors);
        }).ToList();

        var gates = _gates.Select(kv => new TeamSidebarGate(kv.Key, kv.Value)).ToList();

        return new TeamSidebarContent(TeamName, Phase, tasks, _decisions.ToList(), files, gates, _milestones.ToList());
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
