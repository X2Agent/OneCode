using System.Text;
using OneCode.Core.Goals;

namespace OneCode.WebUI.Services;

/// <summary>消息角色。</summary>
public enum WebRole { User, Assistant, System }

/// <summary>一条对话消息。</summary>
/// <remarks>
/// 流式正文/思考以 <see cref="StringBuilder"/> 增量累积，读取时才物化成字符串并缓存：
/// 直接对属性做 <c>Content += delta</c> 是每 token O(n) 复制，长回复退化为 O(n²)
/// 并持续产出大临时字符串。
/// 物化与追加以实例锁互斥：事件流在后台线程逐 token 追加，Blazor 渲染在电路线程读取，
/// <c>StringBuilder</c> 不是线程安全类型——无锁并发读写下 <c>ToString()</c> 会产出撕裂文本甚至抛异常。
/// </remarks>
public sealed class WebMessage(WebRole role, string content)
{
    private readonly object _gate = new();
    private readonly StringBuilder _content = new(content);
    private readonly StringBuilder _thinking = new();
    private string? _cachedContent = content;
    private string? _cachedThinking;

    public WebRole Role { get; } = role;

    public string Content
    {
        get { lock (_gate) return _cachedContent ??= _content.ToString(); }
        set
        {
            lock (_gate)
            {
                _content.Clear();
                _content.Append(value);
                _cachedContent = value;
            }
        }
    }

    /// <summary>该轮回复的思考过程（为空表示没有）。</summary>
    public string Thinking
    {
        get { lock (_gate) return _cachedThinking ??= _thinking.ToString(); }
        set
        {
            lock (_gate)
            {
                _thinking.Clear();
                _thinking.Append(value);
                _cachedThinking = value;
            }
        }
    }

    public bool IsStreaming { get; set; }

    /// <summary>追加正文增量（O(Δ)，不复制已有内容）。</summary>
    public void AppendContent(string text)
    {
        lock (_gate)
        {
            _content.Append(text);
            _cachedContent = null;
        }
    }

    /// <summary>追加思考增量。</summary>
    public void AppendThinking(string text)
    {
        lock (_gate)
        {
            _thinking.Append(text);
            _cachedThinking = null;
        }
    }
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
