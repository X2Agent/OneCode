namespace OneCode.App.Tui;

/// <summary>
/// TEAM 侧边栏 interaction for <see cref="ReplShell"/>：
/// 消费 Team 相关 TuiEvent 维护 <see cref="TeamRunSnapshot"/>，驱动右侧
/// <see cref="TeamSidebarView"/>（纯显示状态板）。
///
/// 显示策略与 Plan 侧边栏一致：首个有实质内容的 Team 事件（审批/协调/任务进度等）
/// 自动展开，弹出后保持可见（直至运行清除/新会话）；澄清/回答阶段不展开
/// （内容近乎空白）；运行终态保留展示，新一次澄清/审批事件整体重置。
/// Plan/Team 面板互斥：同一时刻最多一个可见，避免叠加扣宽挤没对话列。
/// </summary>
public sealed partial class ReplShell
{
    private TeamRunSnapshot? _activeTeamRun;

    /// <summary>Whether the right TEAM sidebar is currently visible.</summary>
    internal bool IsTeamSidebarVisible => _teamSidebar.Visible;

    /// <summary>
    /// 消费一个 TUI 事件：Team 相关事件更新快照并重渲侧边栏，其余事件 no-op。
    /// 在 <see cref="OneCodeToplevel.DispatchEvent"/> 最前端调用（先于对话流渲染）。
    /// </summary>
    public void UpdateTeamSidebar(TuiEvent evt)
    {
        if (!IsTeamSidebarEvent(evt))
            return;

        // 终态后的新澄清/审批事件 = 新一次团队运行，整体重置快照。
        if (_activeTeamRun is { IsTerminal: true } && evt is TuiTeamProgress or TuiTeamPlanApproval)
            _activeTeamRun = null;

        _activeTeamRun ??= new TeamRunSnapshot();
        _activeTeamRun.Apply(evt);

        // 澄清/回答阶段不自动展开：此时快照只有团队名与阶段，弹出一个近乎空白的
        // 面板观感很差（左侧向导卡片已承载澄清交互）。等首个有实质内容的事件
        // （审批/协调/任务进度/文件变更/交付）才自动展开；用户仍可 Ctrl+G 显式展开。
        if (ShouldAutoShowSidebar(evt))
            SetTeamSidebarVisible(true);

        RenderActiveTeamSidebar();
    }

    private static bool ShouldAutoShowSidebar(TuiEvent evt) => evt switch
    {
        // 澄清提问与用户作答属于左侧向导阶段，不足以撑起侧边栏内容。
        TuiTeamProgress or TuiTeamUserResponse => false,
        _ => true,
    };

    private static bool IsTeamSidebarEvent(TuiEvent evt) => evt switch
    {
        TuiTeamProgress or TuiTeamUserResponse or TuiTeamPlanApproval
            or TuiTeamTaskProgress or TuiAgentCoordination or TuiTeamDelivery => true,
        // 文件变更仅统计 TEAM 成员的修改（主对话路径 AgentName 为 null）。
        TuiFileChange { AgentName: { Length: > 0 } } => true,
        _ => false,
    };

    private void RenderActiveTeamSidebar()
    {
        if (_activeTeamRun is not { } run)
            return;

        var content = run.ToContent();
        var headerTitle = string.IsNullOrEmpty(content.TeamName)
            ? content.Phase
            : $"{content.TeamName} · {content.Phase}";
        var lines = ChatBlockRenderers.RenderTeamSidebar(content, _teamSidebar.CurrentWidth - 2);
        _teamSidebar.Update(lines, headerTitle);
    }

    /// <summary>Clears the TEAM run snapshot and hides the sidebar.</summary>
    public void ClearTeamRun()
    {
        _activeTeamRun = null;
        _teamSidebar.ClearContent();
        SetTeamSidebarVisible(false);
    }

    private void SetTeamSidebarVisible(bool visible)
    {
        if (_teamSidebar.Visible == visible)
            return;
        if (visible)
            SetPlanSidebarVisible(false); // 面板互斥：展开 TEAM 时收起 Plan
        _teamSidebar.Visible = visible;
        ApplySidebarLayout();
        _transcript.RequestContentRerender();
    }
}
