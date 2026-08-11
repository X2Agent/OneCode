// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Session;
using OneCode.Infrastructure.Config;
using System.Text;

namespace OneCode.App.Services.Context;

/// <summary>
/// Conditionally injects the project's <c>DESIGN.md</c> into the LLM context when the current
/// task is design-related (frontend, UI, styling, layout, TUI, etc.).
///
/// <para>
/// <see cref="ContextBuilder"/> always loads <c>AGENTS.md</c> into the system prompt. This provider
/// handles the conditional <c>DESIGN.md</c> case: it scans the latest user messages and recently
/// referenced file paths for design signals (keywords + file extensions), and only when triggered
/// reads <c>DESIGN.md</c> fresh from disk and injects it as a system message. This avoids wasting
/// context budget on backend-only tasks.
/// </para>
/// </summary>
public sealed class DesignContextProvider : ReadOnlyAIContextProviderBase
{
    private readonly ISessionConversationAccess? _sessionManager;
    private readonly ILogger<DesignContextProvider> _logger;
    private readonly string _workingDirectory;
    private readonly SessionId? _conversationId;

    // Keywords that signal a design-related task. Mixed CN/EN to match the project's bilingual usage.
    private static readonly string[] DesignKeywords =
    {
        "design", "设计", "UI", "界面", "前端", "frontend",
        "样式", "style", "组件", "component", "页面", "page",
        "布局", "layout", "主题", "theme", "tui", "终端界面",
        "配色", "color", "圆角", "卡片", "弹窗", "dialog",
        "html", "css", "scss", "react", "vue", "blazor", "tailwind"
    };

    // File extensions that indicate design/frontend work.
    private static readonly string[] DesignFileExtensions =
    {
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        ".vue", ".tsx", ".jsx", ".svelte", ".astro"
    };

    // Design-related filename fragments (matched case-insensitively in any path).
    private static readonly string[] DesignFileNameFragments =
    {
        "design", "tui-design", "ui-spec", "style-guide", "design-system"
    };

    private const int RecentUserMessageScanCount = 3;
    private const int RecentAssistantMessageScanCount = 6;

    public DesignContextProvider(
        ISessionConversationAccess? sessionManager,
        ILogger<DesignContextProvider> logger,
        string workingDirectory,
        SessionId? conversationId = null)
    {
        _sessionManager = sessionManager;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _conversationId = conversationId;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Gate 1: only inject when the current task looks design-related.
        if (!IsDesignRelatedContext())
            return new AIContext();

        // Gate 2: DESIGN.md must exist on disk.
        var (path, content) = await ReadDesignFileAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            return new AIContext();

        var sb = new StringBuilder();
        sb.AppendLine("## Design Specification");
        sb.AppendLine("The project's DESIGN.md is loaded below. Follow these design guidelines when working on UI/design/frontend tasks:");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {path}");
        sb.AppendLine();
        sb.AppendLine(content.Trim());

        _logger.LogDebug("Injected DESIGN.md ({Path}) into context", path);

        return new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, sb.ToString())],
        };
    }

    private bool IsDesignRelatedContext()
    {
        var conversation = _conversationId is { } id
            ? _sessionManager?.GetConversation(id)
            : _sessionManager?.ForegroundConversation;
        if (conversation is null)
            return false;

        // Scan recent user messages for design keywords.
        var recentUserMessages = conversation.Messages
            .OfType<UserMessage>()
            .TakeLast(RecentUserMessageScanCount)
            .Select(m => m.Content ?? string.Empty);

        foreach (var msg in recentUserMessages)
        {
            if (ContainsDesignKeyword(msg))
                return true;
        }

        // Scan recent assistant messages for design file references (Read/Edit tool calls mention paths).
        var recentAssistantMessages = conversation.Messages
            .OfType<AssistantMessage>()
            .TakeLast(RecentAssistantMessageScanCount);

        foreach (var msg in recentAssistantMessages)
        {
            foreach (var block in msg.Content)
            {
                if (block is TextBlock tb && ContainsDesignFileReference(tb.Text))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsDesignKeyword(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var keyword in DesignKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ContainsDesignFileReference(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var ext in DesignFileExtensions)
        {
            if (text.Contains(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var fragment in DesignFileNameFragments)
        {
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<(string Path, string Content)> ReadDesignFileAsync(CancellationToken ct)
    {
        var candidates = new[]
        {
            Path.Combine(_workingDirectory, "DESIGN.md"),
            Path.Combine(_workingDirectory, "design.md"),
            Path.Combine(_workingDirectory, Constants.App.ConfigDirName, "DESIGN.md"),
            Path.Combine(_workingDirectory, Constants.App.ConfigDirName, "design.md"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                    return (path, content);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Access denied reading DESIGN.md at {Path}", path);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed to read DESIGN.md at {Path}", path);
            }
        }

        return (string.Empty, string.Empty);
    }
}
