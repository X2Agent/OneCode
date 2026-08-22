using OneCode.App.Services;
using OneCode.App.Services.Lsp;
using OneCode.Core.Commands;
using OneCode.Core.IO;
using OneCode.Core.Keybindings;
using OneCode.Core.Lsp;
using OneCode.Core.Models;
using OneCode.Infrastructure.Media;

namespace OneCode.App.Tui;

/// <summary>Query execution and command dispatch dependencies.</summary>
public sealed record TuiQueryServices(
    Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TuiEvent>> StreamQuery,
    Func<CancellationToken, Task> CreateSession,
    Func<string, CancellationToken, Task<string?>> ExecuteCommand,
    Func<bool>? IsExitRequested = null,
    Func<string, bool>? IsImmediateCommand = null,
    Func<string, string?>? GetProgressMessage = null,
    Func<string, CancellationToken, Task<CommandDispatchResult?>>? TryResolvePromptCommand = null,
    Func<string, string[]?, CancellationToken, IAsyncEnumerable<TuiEvent>>? StreamCommandPrompt = null,
    Func<string, WorkflowResumeKind, CancellationToken, IAsyncEnumerable<TuiEvent>>? StreamResumeWorkflow = null,
    InputQueue? InputQueue = null,
    Func<CancellationToken, Task<TuiBuildRunState?>>? ReplayCurrentBuildRun = null);

/// <summary>Current session, team, and workspace dependencies.</summary>
public sealed record TuiSessionServices(
    Func<IReadOnlyList<string>>? GetSessionUserPrompts = null,
    Func<string?>? GetSessionName = null,
    Func<string?>? GetActiveTeam = null,
    Func<IReadOnlyList<string>>? GetRegisteredTeams = null,
    Func<string?>? CycleTeam = null,
    Func<string, string?>? GetTeamModeLabel = null,
    OneCode.Core.Commands.IGitHelper? GitHelper = null);

/// <summary>LSP status and diagnostic subscriptions.</summary>
public sealed record TuiDiagnosticServices(
    Func<IReadOnlyList<LspServerStatus>>? GetLspServerStatus = null,
    Func<IReadOnlyList<LspDiagnostic>>? GetLspDiagnostics = null,
    Action<Action>? SubscribeDiagnosticsChanged = null,
    Action<Action>? UnsubscribeDiagnosticsChanged = null);

/// <summary>Runtime collaborators shared by visual components.</summary>
public sealed record TuiRuntimeServices(
    string Model,
    IModelCatalog ModelCatalog,
    WorkingModeController? ModeController = null,
    KeybindingResolver? KeyResolver = null,
    KeybindingContextManager? KeyContextManager = null,
    Func<IReadOnlyList<KeybindingWarning>>? GetKeybindingWarnings = null,
    TrustService? TrustService = null,
    ImagePipeline? ImagePipeline = null,
    Func<string>? RecordCost = null,
    OneCode.Core.IO.IClipboardService? Clipboard = null,
    Func<IReadOnlyCollection<string>>? GetToolNames = null,
    Func<bool>? GetShowThinking = null,
    Action<TuiEvent>? EmitEvent = null);

/// <summary>Immutable labels and launch options for one TUI run.</summary>
public sealed record TuiLaunchOptions(
    string Version,
    CancellationToken ExternalCancellation,
    IReadOnlyList<SlashCommandEntry> SlashCommands,
    string? SshHost = null,
    string? InitialPrompt = null);

/// <summary>
/// Grouped dependencies passed from <see cref="TuiHost"/> to <see cref="OneCodeToplevel"/>.
/// Forwarding properties keep consumers concise while construction remains organized by responsibility.
///
/// 架构决策（tui-refactor D4 保守分支）：叶子组件（ReplShell / ChatInputView 等）
/// 一律按组件显式注入所需依赖，不接收本对象；TuiContext 的转发外观只服务
/// OneCodeToplevel 一个叶子消费者，不引入按消费方的 ISP 接口拆分。
/// </summary>
public sealed record TuiContext(
    TuiQueryServices Query,
    TuiSessionServices Session,
    TuiDiagnosticServices Diagnostics,
    TuiRuntimeServices Runtime,
    TuiLaunchOptions Options)
{
    public Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TuiEvent>> StreamQuery => Query.StreamQuery;
    public Func<CancellationToken, Task> CreateSession => Query.CreateSession;
    public Func<string, CancellationToken, Task<string?>> ExecuteCommand => Query.ExecuteCommand;
    public Func<bool>? IsExitRequested => Query.IsExitRequested;
    public Func<string, bool>? IsImmediateCommand => Query.IsImmediateCommand;
    public Func<string, string?>? GetProgressMessage => Query.GetProgressMessage;
    public Func<string, CancellationToken, Task<CommandDispatchResult?>>? TryResolvePromptCommand => Query.TryResolvePromptCommand;
    public Func<string, string[]?, CancellationToken, IAsyncEnumerable<TuiEvent>>? StreamCommandPrompt => Query.StreamCommandPrompt;
    public Func<string, WorkflowResumeKind, CancellationToken, IAsyncEnumerable<TuiEvent>>? StreamResumeWorkflow => Query.StreamResumeWorkflow;
    public InputQueue? InputQueue => Query.InputQueue;
    public Func<CancellationToken, Task<TuiBuildRunState?>>? ReplayCurrentBuildRun => Query.ReplayCurrentBuildRun;

    public Func<IReadOnlyList<string>>? GetSessionUserPrompts => Session.GetSessionUserPrompts;
    public Func<string?>? GetSessionName => Session.GetSessionName;
    public Func<string?>? GetActiveTeam => Session.GetActiveTeam;
    public Func<IReadOnlyList<string>>? GetRegisteredTeams => Session.GetRegisteredTeams;
    public Func<string?>? CycleTeam => Session.CycleTeam;
    public Func<string, string?>? GetTeamModeLabel => Session.GetTeamModeLabel;
    public IGitHelper? GitHelper => Session.GitHelper;

    public Func<IReadOnlyList<LspServerStatus>>? GetLspServerStatus => Diagnostics.GetLspServerStatus;
    public Func<IReadOnlyList<LspDiagnostic>>? GetLspDiagnostics => Diagnostics.GetLspDiagnostics;
    public Action<Action>? SubscribeDiagnosticsChanged => Diagnostics.SubscribeDiagnosticsChanged;
    public Action<Action>? UnsubscribeDiagnosticsChanged => Diagnostics.UnsubscribeDiagnosticsChanged;

    public string Model => Runtime.Model;
    public IModelCatalog ModelCatalog => Runtime.ModelCatalog;
    public WorkingModeController? ModeController => Runtime.ModeController;
    public KeybindingResolver? KeyResolver => Runtime.KeyResolver;
    public KeybindingContextManager? KeyContextManager => Runtime.KeyContextManager;
    public Func<IReadOnlyList<KeybindingWarning>>? GetKeybindingWarnings => Runtime.GetKeybindingWarnings;
    public TrustService? TrustService => Runtime.TrustService;
    public ImagePipeline? ImagePipeline => Runtime.ImagePipeline;
    public Func<string>? RecordCost => Runtime.RecordCost;
    public IClipboardService? Clipboard => Runtime.Clipboard;
    public Func<IReadOnlyCollection<string>>? GetToolNames => Runtime.GetToolNames;
    public Func<bool>? GetShowThinking => Runtime.GetShowThinking;
    public Action<TuiEvent>? EmitEvent => Runtime.EmitEvent;

    public string Version => Options.Version;
    public CancellationToken ExternalCancellation => Options.ExternalCancellation;
    public IReadOnlyList<SlashCommandEntry> SlashCommands => Options.SlashCommands;
    public string? SshHost => Options.SshHost;
    public string? InitialPrompt => Options.InitialPrompt;
}
