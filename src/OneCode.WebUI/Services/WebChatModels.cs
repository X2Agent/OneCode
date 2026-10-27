using OneCode.Core.Goals;

namespace OneCode.WebUI.Services;

/// <summary>消息角色。</summary>
public enum WebRole { User, Assistant, System }

/// <summary>一条对话消息。</summary>
public sealed class WebMessage(WebRole role, string content)
{
    public WebRole Role { get; } = role;

    public string Content { get; set; } = content;

    /// <summary>该轮回复的思考过程（为空表示没有）。</summary>
    public string Thinking { get; set; } = string.Empty;

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
