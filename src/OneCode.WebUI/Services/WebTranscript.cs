using OneCode.App.Query;
using OneCode.Core.Domain;

namespace OneCode.WebUI.Services;

/// <summary>
/// 转录渲染状态的唯一持有者：消息、工具调用、活动与上下文清单。
/// 接收 <see cref="QueryEvent"/> 增量、把会话历史投影为可见消息、支撑整体替换（切换/新建/关闭会话）。
/// 线程安全：所有变更在内部锁内完成，UI 线程经只读快照消费。
/// </summary>
public sealed class WebTranscript
{
    private readonly List<WebMessage> _messages = [];
    private readonly List<WebToolCall> _toolCalls = [];
    private readonly List<WebActivity> _activities = [];
    private readonly List<WebContextItem> _context = [];
    private readonly object _gate = new();

    public IReadOnlyList<WebMessage> Messages { get { lock (_gate) return [.. _messages]; } }

    public IReadOnlyList<WebToolCall> ToolCalls { get { lock (_gate) return [.. _toolCalls]; } }

    public IReadOnlyList<WebActivity> Activities { get { lock (_gate) return [.. _activities]; } }

    /// <summary>会话级上下文：读写过的文件与访问过的链接（切换会话后由历史重建）。</summary>
    public IReadOnlyList<WebContextItem> ContextItems { get { lock (_gate) return [.. _context]; } }

    /// <summary>最近一轮完成的用量（DoneEvent 回填，切换/重置转录时清零）。</summary>
    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    /// <summary>把一条 <see cref="QueryEvent"/> 增量应用到渲染状态。</summary>
    public void Apply(QueryEvent evt, ILogger logger)
    {
        switch (evt)
        {
            case TextDeltaEvent delta:
                AppendAssistant(delta.Text);
                break;

            case ThinkingDeltaEvent thinking:
                AppendThinking(thinking.Text);
                break;

            case ToolStartEvent start:
                lock (_gate)
                {
                    _toolCalls.Add(new WebToolCall(start.ToolId, start.ToolName, start.ToolInput, start.AgentName));
                    WebContextTracker.AccumulateToolUse(_context, start.ToolName, start.ToolInput, logger);
                }
                break;

            case ToolDoneEvent done:
                lock (_gate)
                {
                    var call = _toolCalls.FirstOrDefault(c => c.ToolId == done.ToolId);
                    if (call is null)
                    {
                        call = new WebToolCall(done.ToolId, done.ToolName, done.ToolInput, done.AgentName);
                        _toolCalls.Add(call);
                    }
                    call.Complete(done.IsError, done.Result);
                }
                break;

            case SubagentProgressEvent progress:
                UpsertActivity($"subagent:{progress.ForkId}", "子代理",
                    $"{progress.AgentName} · {progress.Phase} · {progress.CurrentToolName ?? "运行中"}");
                break;

            case ModeProgressEvent mode:
                UpsertActivity("mode", "进度", mode.Message);
                break;

            case FileChangeEvent change:
                lock (_gate)
                    WebContextTracker.AccumulateFileChange(_context, change);
                break;

            case NoticeEvent notice:
                AddSystem(notice.Message);
                break;

            case ErrorEvent error:
                AddSystem($"⚠ {error.Message}");
                break;

            case CompactSuggestedEvent compact:
                AddSystem($"💡 {compact.Message}");
                break;

            case BudgetWarningEvent budget:
                AddSystem($"⚠ 预算预警：已消耗 {budget.UsedTokens:N0} / {budget.LimitTokens:N0} tokens");
                break;

            case GoalBudgetWarningEvent goalBudget:
                AddSystem($"⚠ Goal 预算预警（{goalBudget.Level}）：attempts={goalBudget.TotalAttempts}, tokens={goalBudget.TotalTokens}");
                break;

            case GoalPlanEvent plan:
                AddSystem($"已分解为 {plan.Steps.Count} 个子目标");
                lock (_gate)
                {
                    _activities.RemoveAll(a => a.Key == "goal-plan");
                    _activities.Add(new WebActivity("goal-plan", "Goal 计划", string.Join(" / ", plan.Steps)));
                }
                break;

            case GoalPlanUpdatedEvent planUpdated:
                UpsertActivity("goal-plan-updated", "Goal 进度",
                    $"第 {planUpdated.CurrentIndex + 1} / {planUpdated.Steps.Count} 步");
                break;

            case GoalResultEvent goalResult:
                AddSystem($"Goal 结束：✓ {goalResult.Completed} · ✗ {goalResult.Failed} · ○ {goalResult.Skipped}");
                break;

            case BuildRunStateEvent build:
                UpsertActivity("build", "Build",
                    $"{build.State} · {build.CompletedTasks}/{build.TotalTasks}");
                break;

            case BuildRunCompletedEvent:
                AddSystem("Build 运行已结束，产物清单已生成。");
                break;

            case WorkflowRunStartedEvent workflow:
                AddSystem($"已批准计划 {workflow.PlanId}（修订 {workflow.Revision}），开始执行");
                break;

            case WorkflowRunEndedEvent workflowEnded:
                AddSystem($"Build run {workflowEnded.PlanId} 结束");
                break;

            case DoneEvent done:
                InputTokens = done.Usage?.InputTokens ?? 0;
                OutputTokens = done.Usage?.OutputTokens ?? 0;
                break;

            case WorkingDirectoryChangedEvent cwd:
                UpsertActivity("cwd", "工作目录", cwd.WorkingDirectory);
                break;

            case AgentMessageEvent agent:
                AddSystem($"[{agent.AgentName}] {agent.Content}");
                break;

            case SuggestionsEvent or TurnStartedEvent or TurnCompletedEvent:
                break;

            default:
                // 契约新增子类型未渲染时的兜底：基线由 QueryEventContractTests 守卫，这里不静默丢弃。
                logger.LogWarning(
                    "未渲染的 QueryEvent 子类型 {EventType}：请补 TUI / WebUI 两处渲染并更新契约基线",
                    evt.GetType().Name);
                break;
        }
    }

    /// <summary>把流式标记中的消息收尾为已完结（每轮运行结束时调用）。</summary>
    public void FinishStreaming()
    {
        lock (_gate)
        {
            foreach (var message in _messages.Where(m => m.IsStreaming))
                message.IsStreaming = false;
        }
    }

    /// <summary>整体替换转录与上下文清单，并重置本会话用量计数。</summary>
    public void Replace(IReadOnlyList<WebMessage> messages, IReadOnlyList<WebContextItem> context)
    {
        lock (_gate)
        {
            _messages.Clear();
            _messages.AddRange(messages);
            _toolCalls.Clear();
            _activities.Clear();
            _context.Clear();
            _context.AddRange(context);
        }
        InputTokens = 0;
        OutputTokens = 0;
    }

    /// <summary>清空全部渲染状态（新建/关闭会话时调用）。</summary>
    public void Reset() => Replace([], []);

    /// <summary>把会话历史投影为可见消息与上下文清单后整体替换当前转录。</summary>
    public void LoadHistory(Conversation conversation, ILogger logger)
    {
        var (messages, context) = Project(conversation, logger);
        Replace(messages, context);
    }

    private (List<WebMessage> Messages, List<WebContextItem> Context) Project(Conversation conversation, ILogger logger)
    {
        List<WebMessage> messages = [];
        List<WebContextItem> context = [];
        foreach (var message in conversation.Messages)
        {
            switch (message)
            {
                case UserMessage { IsMeta: false } user:
                    messages.Add(new WebMessage(WebRole.User, user.Content));
                    break;
                case AssistantMessage assistant:
                    var text = string.Concat(assistant.Content.OfType<TextBlock>().Select(b => b.Text));
                    var thinking = string.Concat(assistant.Content.OfType<ThinkingBlock>().Select(b => b.Thinking));
                    if (text.Length > 0 || thinking.Length > 0)
                        messages.Add(new WebMessage(WebRole.Assistant, text) { Thinking = thinking });

                    foreach (var toolUse in assistant.Content.OfType<ToolUseBlock>())
                        WebContextTracker.AccumulateToolUse(context, toolUse.Name, toolUse.Input, logger);
                    break;
                case SystemMessage system:
                    messages.Add(new WebMessage(WebRole.System, system.Content));
                    break;
            }
        }

        return (messages, context);
    }

    public void AddUser(string text)
    {
        lock (_gate)
            _messages.Add(new WebMessage(WebRole.User, text));
    }

    /// <summary>追加一条空的流式助手消息：本轮文本/思考增量都挂到它上面。</summary>
    public void BeginStreamingReply()
    {
        lock (_gate)
            _messages.Add(new WebMessage(WebRole.Assistant, string.Empty) { IsStreaming = true });
    }

    private void AppendAssistant(string text)
    {
        lock (_gate)
            GetOrCreateStreamingAssistant().Content += text;
    }

    /// <summary>思考增量挂到当前这一轮的助手消息上：思考属于该轮回复，不能脱离消息流单独累积。</summary>
    private void AppendThinking(string text)
    {
        lock (_gate)
            GetOrCreateStreamingAssistant().Thinking += text;
    }

    private WebMessage GetOrCreateStreamingAssistant()
    {
        var last = _messages.LastOrDefault();
        if (last is null || last.Role != WebRole.Assistant || !last.IsStreaming)
        {
            last = new WebMessage(WebRole.Assistant, string.Empty) { IsStreaming = true };
            _messages.Add(last);
        }

        return last;
    }

    public void AddSystem(string text)
    {
        lock (_gate)
            _messages.Add(new WebMessage(WebRole.System, text));
    }

    private void UpsertActivity(string key, string kind, string text)
    {
        lock (_gate)
        {
            var existing = _activities.FirstOrDefault(a => a.Key == key);
            if (existing is null)
                _activities.Add(new WebActivity(key, kind, text));
            else
                existing.Text = text;
        }
    }
}
