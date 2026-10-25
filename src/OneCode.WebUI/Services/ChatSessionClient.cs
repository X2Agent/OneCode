using OneCode.App.Commands;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Interaction;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;

namespace OneCode.WebUI.Services;

/// <summary>
/// 每个浏览器电路一个会话客户端：组装交互会话、消费 <see cref="QueryEvent"/> 流、
/// 维护渲染状态并把交互请求映射为中断卡片。
/// </summary>
public sealed class ChatSessionClient(
    WebChatDependencies deps,
    WebInteractionDispatcher dispatcher,
    MarkdownRenderer markdown,
    RunEventBridge runEvents,
    ILogger<ChatSessionClient> logger) : IAsyncDisposable
{
    private IDisposable? _runEventsSubscription;

    private readonly List<WebMessage> _messages = [];
    private readonly List<WebToolCall> _toolCalls = [];
    private readonly List<WebActivity> _activities = [];
    private readonly object _gate = new();

    private InteractiveSession? _session;
    private TaskCompletionSource<ApprovalDecision>? _pendingApproval;
    private TaskCompletionSource<string?>? _pendingQuestion;
    private TaskCompletionSource<GoalBudgetDecision>? _pendingBudget;
    private CancellationTokenSource? _runCts;
    private string _thinking = string.Empty;
    // 本电路的稳定标识：交互接收者按电路注册，路由到活跃电路。
    private readonly string _circuitId = Guid.NewGuid().ToString("N");

    /// <summary>状态变化通知；组件订阅后触发重绘。</summary>
    public event Action? Changed;

    public IReadOnlyList<WebMessage> Messages { get { lock (_gate) return [.. _messages]; } }

    public IReadOnlyList<WebToolCall> ToolCalls { get { lock (_gate) return [.. _toolCalls]; } }

    public IReadOnlyList<WebActivity> Activities { get { lock (_gate) return [.. _activities]; } }

    public string Thinking { get { lock (_gate) return _thinking; } }

    public WebPendingInteraction? Pending { get; private set; }

    public bool IsRunning { get; private set; }

    public string? ModelName { get; private set; }

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public string Mode => deps.ModeController.Mode.ToString().ToUpperInvariant();

    /// <summary>初始化会话：确保活跃会话、构建系统提示与 harness、注册交互接收者。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var modelId = deps.ModelManager.GetMainModel(deps.AppState.Current.MainLoopModel).Id;
        await deps.SessionManager.EnsureActiveSessionAsync(
            new ConversationOptions(Environment.CurrentDirectory, modelId), ct).ConfigureAwait(false);

        var systemPrompt = await deps.BuildSystemPrompt(ct).ConfigureAwait(false);
        var harnessPrompt = await deps.LoadHarness(ct).ConfigureAwait(false);
        var commands = deps.CommandRegistry.GetAll()
            .Select(c => new SlashCommandEntry(c.Name, c.Description, c.Source, c.ArgumentHint))
            .ToList();

        _session = new InteractiveSession(
            deps.Runner, systemPrompt, deps.SessionManager, deps.ModeController, commands, harnessPrompt);
        ModelName = modelId;

        // 电路打开即注册交互接收者并抢占活跃电路：多窗口场景下交互卡片只投给用户当前操作的窗口。
        dispatcher.Register(_circuitId, OnInteraction);
        dispatcher.MarkActive(_circuitId);

        // 旁路事件（已批准计划的 Build run 不经查询流）：每个浏览器电路独立订阅，销毁时释放。
        _runEventsSubscription?.Dispose();
        _runEventsSubscription = runEvents.Subscribe(evt =>
        {
            Apply(evt);
            Notify();
        });

        await RefreshSessionsAsync(ct).ConfigureAwait(false);

        // 与 TUI 启动同语义：回放当前会话最近一次 BuildRun 快照，
        // 避免 Web 端 Plan/Build 状态投影缺失历史（不重新执行工具、无副作用）。
        if (deps.SessionManager.ForegroundConversation is { } foreground)
        {
            var buildRun = await deps.BuildRunReplay.ReplayLatestAsync(foreground.Id, ct).ConfigureAwait(false);
            if (buildRun is not null)
                Apply(buildRun);
        }

        Notify();
    }

    /// <summary>侧栏会话列表（最近活跃在前）。</summary>
    public IReadOnlyList<WebSessionItem> Sessions { get; private set; } = [];

    /// <summary>刷新侧栏会话列表。</summary>
    public async Task RefreshSessionsAsync(CancellationToken ct = default)
    {
        var foregroundId = deps.SessionManager.ForegroundConversation?.Id;
        var summaries = await deps.SessionManager.ListAsync(ct).ConfigureAwait(false);
        Sessions = summaries
            .Select(s => new WebSessionItem(
                s.Id.ToString(),
                s.Title ?? "(未命名)",
                s.Mode ?? "build",
                s.MessageCount,
                s.LastActivityAt,
                s.Id == foregroundId))
            .ToList();
        Notify();
    }

    /// <summary>切换前台会话并把该会话历史回放进转录。</summary>
    public async Task SwitchSessionAsync(string conversationId, CancellationToken ct = default)
    {
        var conversation = await deps.SessionManager.SwitchToSessionAsync(conversationId, ct).ConfigureAwait(false);
        if (conversation is null)
        {
            AddSystem($"会话 {conversationId} 不存在。");
            Notify();
            return;
        }

        await ReplayTranscriptAsync(conversation.Id.ToString(), ct).ConfigureAwait(false);

        // 切换后同步回放新前台会话的 BuildRun 快照（与初始化同语义）。
        var buildRun = await deps.BuildRunReplay.ReplayLatestAsync(conversation.Id, ct).ConfigureAwait(false);
        if (buildRun is not null)
            Apply(buildRun);

        await RefreshSessionsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>新建会话：当前会话转入后台（可经 <c>/session switch</c> 恢复），对齐 <c>/new</c> 语义。</summary>
    public async Task NewSessionAsync(CancellationToken ct = default)
    {
        var modelId = deps.ModelManager.GetMainModel(deps.AppState.Current.MainLoopModel).Id;
        var cwd = deps.SessionManager.ForegroundConversation?.WorkingDirectory ?? Directory.GetCurrentDirectory();
        await deps.SessionManager.BackgroundCurrentAndCreateNewAsync(
            new ConversationOptions(cwd, modelId), ct).ConfigureAwait(false);

        ResetTranscript();
        AddSystem("已创建新会话，原会话转入后台。");
        await RefreshSessionsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>关闭当前前台会话；下一条消息会惰性开启新会话（与 <c>/close</c> 语义一致）。</summary>
    public async Task CloseCurrentAsync(CancellationToken ct = default)
    {
        var foregroundId = deps.SessionManager.ForegroundConversation?.Id;
        if (foregroundId is null)
        {
            AddSystem("没有可关闭的活动会话。");
            Notify();
            return;
        }

        await deps.SessionManager.CloseAsync(ct).ConfigureAwait(false);
        ResetTranscript();
        AddSystem($"已关闭会话 {foregroundId}。");
        await RefreshSessionsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>清空转录渲染状态（会话切换/新建/关闭时调用）。</summary>
    private void ResetTranscript()
    {
        lock (_gate)
        {
            _messages.Clear();
            _toolCalls.Clear();
            _activities.Clear();
            _thinking = string.Empty;
        }
        InputTokens = 0;
        OutputTokens = 0;
    }

    /// <summary>把会话历史回放进转录渲染状态（仅用户 / 助手文本 / 系统三类可见消息）。</summary>
    private async Task ReplayTranscriptAsync(string conversationId, CancellationToken ct)
    {
        var history = await deps.SessionManager.ReplayAsync(conversationId, ct).ConfigureAwait(false);
        ResetTranscript();
        lock (_gate)
        {
            foreach (var message in history)
            {
                switch (message)
                {
                    case UserMessage { IsMeta: false } user:
                        _messages.Add(new WebMessage(WebRole.User, user.Content));
                        break;
                    case AssistantMessage assistant:
                        var text = string.Concat(assistant.Content.OfType<TextBlock>().Select(b => b.Text));
                        if (text.Length > 0)
                            _messages.Add(new WebMessage(WebRole.Assistant, text));
                        break;
                    case SystemMessage system:
                        _messages.Add(new WebMessage(WebRole.System, system.Content));
                        break;
                }
            }
        }
        Notify();
    }

    /// <summary>提交一条用户消息或斜杠命令并消费整条事件流。</summary>
    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        if (_session is null || string.IsNullOrWhiteSpace(text))
            return;

        if (text.StartsWith('/'))
        {
            await SendCommandAsync(_session, text, ct).ConfigureAwait(false);
            return;
        }

        BeginRun(ct);
        lock (_gate)
        {
            _messages.Add(new WebMessage(WebRole.User, text));
            _messages.Add(new WebMessage(WebRole.Assistant, string.Empty) { IsStreaming = true });
            _thinking = string.Empty;
        }
        Notify();

        await ConsumeEventsAsync(
            deps.Streams.StreamQueryAsync(_session, text, imagePaths: null, ct: RunToken()),
            "查询").ConfigureAwait(false);
    }

    /// <summary>
    /// 斜杠命令分发，与 TUI 宿主同一语义：命令产出的 prompt / 工作流恢复 / 循环任务
    /// 经同一条查询流水线流出；其余命令本地执行并显示结果文本，未知命令显示建议文案。
    /// Overlay 类命令（/find、/diff 等）由命令自身的非 TUI 降级文案呈现；
    /// <c>/exit</c> 只显示结果——退出会终止浏览器共享的服务进程。
    /// </summary>
    private async Task SendCommandAsync(InteractiveSession session, string text, CancellationToken ct)
    {
        BeginRun(ct);
        lock (_gate)
            _messages.Add(new WebMessage(WebRole.User, text));
        Notify();

        try
        {
            // TryResolvePromptCommandAsync 会真实执行命令：PromptResult / ResumeWorkflowResult /
            // LoopResult 以流形式呈现，其余结果缓存下来由 ExecuteCommandAsync 取用显示。
            // 缓存交接依赖同一管线实例，本轮内只新建一次。
            var commands = deps.CreateCommands();
            var dispatch = await commands.TryResolvePromptCommandAsync(session, text, ct).ConfigureAwait(false);
            switch (dispatch)
            {
                case CommandDispatchResult.Prompt prompt:
                    await ConsumeEventsAsync(
                        deps.Streams.StreamCommandPromptAsync(session, prompt.Content, prompt.AllowedTools, ct: RunToken()),
                        "命令查询").ConfigureAwait(false);
                    return;

                case CommandDispatchResult.ResumeWorkflow resume:
                    await ConsumeEventsAsync(
                        deps.Streams.StreamResumeWorkflowAsync(session, resume.SessionId, resume.Kind, ct: RunToken()),
                        "工作流恢复").ConfigureAwait(false);
                    return;

                case CommandDispatchResult.Loop loop:
                    await ConsumeEventsAsync(
                        deps.Streams.StreamLoopAsync(session, loop.Task, loop.CheckCommand, loop.MaxIterations, ct: RunToken()),
                        "循环任务").ConfigureAwait(false);
                    return;
            }

            var result = await commands.ExecuteCommandAsync(session, text, ct).ConfigureAwait(false);
            if (result is not null)
                AddSystem(result);
        }
        catch (OperationCanceledException)
        {
            AddSystem("已停止。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Web 斜杠命令执行失败");
            AddSystem($"命令执行失败：{ex.Message}");
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>开启一轮运行：关联取消令牌并标记忙碌；提交窗口同时抢占活跃电路。</summary>
    private void BeginRun(CancellationToken ct)
    {
        // 提交查询的窗口成为活跃电路：后台线程到达的交互请求（审批/提问/预算）路由到这里。
        dispatcher.MarkActive(_circuitId);
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
    }

    private CancellationToken RunToken() => _runCts?.Token ?? throw new InvalidOperationException("运行尚未开始。");

    private void EndRun()
    {
        IsRunning = false;
        lock (_gate)
        {
            foreach (var message in _messages.Where(m => m.IsStreaming))
                message.IsStreaming = false;
        }
        _runCts?.Dispose();
        _runCts = null;
        Notify();
    }

    /// <summary>消费一条 <see cref="QueryEvent"/> 流并渲染；查询、命令与工作流恢复共用。</summary>
    private async Task ConsumeEventsAsync(IAsyncEnumerable<QueryEvent> events, string source)
    {
        try
        {
            await foreach (var evt in events.ConfigureAwait(false))
            {
                Apply(evt);
                Notify();
            }
        }
        catch (OperationCanceledException)
        {
            AddSystem("已停止。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Web {Source}流异常终止", source);
            AddSystem($"运行失败：{ex.Message}");
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>停止当前运行。</summary>
    public void Stop() => _runCts?.Cancel();

    /// <summary>回传审批决策。</summary>
    public void RespondApproval(ApprovalDecision decision)
    {
        var pending = _pendingApproval;
        _pendingApproval = null;
        Pending = null;
        pending?.TrySetResult(decision);
        Notify();
    }

    /// <summary>回传提问回答。</summary>
    public void RespondQuestion(string? answer)
    {
        var pending = _pendingQuestion;
        _pendingQuestion = null;
        Pending = null;
        pending?.TrySetResult(answer);
        Notify();
    }

    /// <summary>回传预算决策。</summary>
    public void RespondBudget(GoalBudgetDecision decision)
    {
        var pending = _pendingBudget;
        _pendingBudget = null;
        Pending = null;
        pending?.TrySetResult(decision);
        Notify();
    }

    /// <summary>渲染 Markdown（已禁用原始 HTML）。</summary>
    public string RenderMarkdown(string? text) => markdown.ToHtml(text);

    private void OnInteraction(InteractionRequest request)
    {
        switch (request)
        {
            case ApprovalInteractionRequest approval:
                _pendingApproval = approval.ResponseSource;
                Pending = new WebPendingInteraction(
                    WebInteractionKind.Approval,
                    $"需要批准：{approval.ToolName}",
                    approval.ToolInput,
                    null,
                    AllowAlways: true);
                break;

            case QuestionInteractionRequest question:
                _pendingQuestion = question.ResponseSource;
                Pending = new WebPendingInteraction(
                    WebInteractionKind.Question,
                    question.Question,
                    null,
                    question.Options,
                    AllowAlways: false);
                break;

            case BudgetInteractionRequest budget:
                _pendingBudget = budget.ResponseSource;
                Pending = new WebPendingInteraction(
                    WebInteractionKind.Budget,
                    budget.Context.Scope == BudgetPromptScope.AgentRun ? "执行预算已耗尽" : "Goal 预算已耗尽",
                    budget.Context.Reason,
                    null,
                    AllowAlways: false,
                    BudgetScope: budget.Context.Scope);
                break;

            case WizardInteractionRequest wizard:
                // 向导在 WebUI 首版不支持：立即返回取消，避免调用方悬挂。
                wizard.ResponseSource.TrySetResult(WizardResult.Cancelled);
                logger.LogDebug("WebUI 收到多问题向导请求（{Title}），首版不支持，已返回取消", wizard.Title);
                break;
        }

        Notify();
    }

    private void Apply(QueryEvent evt)
    {
        switch (evt)
        {
            case TextDeltaEvent delta:
                AppendAssistant(delta.Text);
                break;

            case ThinkingDeltaEvent thinking:
                lock (_gate)
                    _thinking += thinking.Text;
                break;

            case ToolStartEvent start:
                lock (_gate)
                    _toolCalls.Add(new WebToolCall(start.ToolId, start.ToolName, start.ToolInput, start.AgentName));
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
                    _activities.Add(new WebActivity($"file:{change.FileName}", "文件变更", change.FileName));
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

    private void AppendAssistant(string text)
    {
        lock (_gate)
        {
            var last = _messages.LastOrDefault();
            if (last is null || last.Role != WebRole.Assistant || !last.IsStreaming)
            {
                last = new WebMessage(WebRole.Assistant, string.Empty) { IsStreaming = true };
                _messages.Add(last);
            }
            last.Content += text;
        }
    }

    private void AddSystem(string text)
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

    private void Notify() => Changed?.Invoke();

    public ValueTask DisposeAsync()
    {
        // 只注销本电路：不误清其他电路的接收者（活跃电路交还逻辑见 WebInteractionDispatcher.Unregister）。
        dispatcher.Unregister(_circuitId);
        _runEventsSubscription?.Dispose();
        _runEventsSubscription = null;
        _runCts?.Cancel();
        _runCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>消息角色。</summary>
public enum WebRole { User, Assistant, System }

/// <summary>一条对话消息。</summary>
public sealed class WebMessage(WebRole role, string content)
{
    public WebRole Role { get; } = role;

    public string Content { get; set; } = content;

    public bool IsStreaming { get; set; }
}

/// <summary>工具调用卡片状态。</summary>
public sealed class WebToolCall(string toolId, string name, string? input, string? agentName)
{
    public string ToolId { get; } = toolId;

    public string Name { get; } = name;

    public string? Input { get; } = input;

    public string? AgentName { get; } = agentName;

    public bool IsRunning { get; private set; } = true;

    public bool IsError { get; private set; }

    public string? Result { get; private set; }

    public void Complete(bool isError, string? result)
    {
        IsRunning = false;
        IsError = isError;
        Result = result;
    }
}

/// <summary>活动抽屉中的一条状态。</summary>
public sealed class WebActivity(string key, string kind, string text)
{
    public string Key { get; } = key;

    public string Kind { get; } = kind;

    public string Text { get; set; } = text;
}

/// <summary>待用户应答的中断类型。</summary>
public enum WebInteractionKind { Approval, Question, Budget }

/// <summary>侧栏会话列表中的一项。</summary>
public sealed record WebSessionItem(
    string Id,
    string Title,
    string Mode,
    int MessageCount,
    DateTimeOffset LastActivityAt,
    bool IsForeground);

/// <summary>中断卡片数据。</summary>
public sealed record WebPendingInteraction(
    WebInteractionKind Kind,
    string Title,
    string? Description,
    IReadOnlyList<string>? Options,
    bool AllowAlways,
    // 预算中断的场景：决定选项集（Goal 子目标工作流或单次 Agent 运行）。
    BudgetPromptScope BudgetScope = BudgetPromptScope.GoalRun);
