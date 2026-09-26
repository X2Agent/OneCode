using System.Runtime.CompilerServices;

using OneCode.App.Tui;
using OneCode.Core.Commands;
using OneCode.Core.Keybindings;

namespace OneCode.Tests.TestSupport.Tui;

/// <summary>
/// 脚本化 <see cref="TuiContext"/> 工厂。
///
/// 交互测试关心「一条输入进来后界面怎么走」，不关心模型返回了什么，
/// 因此这里用一段可编程的 <see cref="TuiEvent"/> 脚本顶替真实查询流，
/// 用命令名集合顶替真实 <c>CommandRegistry</c> 的 <c>Immediate</c> 判定
/// （生产实现见 <c>TuiContextFactory</c>）。
/// </summary>
internal sealed class TuiTestContextBuilder
{
    private readonly List<TuiEvent> _script = [];
    private readonly HashSet<string> _immediateCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SlashCommandEntry> _slashCommands = [];
    private readonly List<string> _sessionPrompts = [];

    private Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TuiEvent>>? _stream;
    private Func<string, CancellationToken, Task<string?>>? _execute;
    private string _model = "test-model";
    private IGitHelper? _gitHelper;
    private IReadOnlyList<KeybindingWarning> _keybindingWarnings = [];

    private readonly List<Action> _sessionCreatedObservers = [];
    private readonly List<Action> _commandExecutedObservers = [];
    private readonly List<Action> _streamExhaustedObservers = [];

    /// <summary>
    /// 观测点：<c>CreateSession</c> 被调用。这是查询流水线唯一「已进入」的确定性信号，
    /// 供 <see cref="TuiTestShell"/> 用它消掉「等待开始流式」的竞态。
    /// </summary>
    public TuiTestContextBuilder OnSessionCreated(Action handler)
    {
        _sessionCreatedObservers.Add(handler);
        return this;
    }

    /// <summary>观测点：<c>ExecuteCommand</c> 返回后（命令文本已可用，尚未落屏）。</summary>
    public TuiTestContextBuilder OnCommandExecuted(Action handler)
    {
        _commandExecutedObservers.Add(handler);
        return this;
    }

    /// <summary>观测点：查询脚本已全部被消费（消费者已处理完最后一条事件）。</summary>
    public TuiTestContextBuilder OnStreamExhausted(Action handler)
    {
        _streamExhaustedObservers.Add(handler);
        return this;
    }

    /// <summary>追加查询流事件：按声明顺序产出，脚本耗尽即流结束。</summary>
    public TuiTestContextBuilder Stream(params TuiEvent[] events)
    {
        _script.AddRange(events);
        return this;
    }

    /// <summary>替换查询流实现 —— 用于脚本表达不了的场景（挂起、抛异常、断言取消）。</summary>
    public TuiTestContextBuilder StreamQuery(
        Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TuiEvent>> stream)
    {
        _stream = stream;
        return this;
    }

    /// <summary>斜杠命令的返回文本（等价于 <c>ExecuteCommand</c> 的结果）。</summary>
    public TuiTestContextBuilder OnCommand(Func<string, string> handler)
    {
        _execute = (text, _) => Task.FromResult<string?>(handler(text));
        return this;
    }

    /// <summary>声明哪些输入走「立即执行」通道，不排队、不打断运行中的查询。</summary>
    public TuiTestContextBuilder Immediate(params string[] names)
    {
        foreach (var name in names)
            _immediateCommands.Add(name);
        return this;
    }

    public TuiTestContextBuilder Model(string model)
    {
        _model = model;
        return this;
    }

    public TuiTestContextBuilder SlashCommand(
        string name,
        string description,
        CommandSource source = CommandSource.Builtin)
    {
        _slashCommands.Add(new SlashCommandEntry(name, description, source));
        return this;
    }

    public TuiTestContextBuilder SessionPrompt(string prompt)
    {
        _sessionPrompts.Add(prompt);
        return this;
    }

    /// <summary>
    /// 注入 Git 辅助服务 —— <c>/diff</c> 审查 overlay 的唯一数据来源（缺失时列出「未检测到变更」）。
    /// </summary>
    public TuiTestContextBuilder GitHelper(IGitHelper helper)
    {
        _gitHelper = helper;
        return this;
    }

    /// <summary>
    /// 注入 keybindings.json 校验警告快照 —— <c>/keybindings list</c> overlay 的「配置警告」区。
    /// 不注入等价于「配置干净」，因此反证用例必须显式注入才能区分两者。
    /// </summary>
    public TuiTestContextBuilder KeybindingWarnings(params KeybindingWarning[] warnings)
    {
        _keybindingWarnings = warnings;
        return this;
    }

    public TuiContext Build() => new(
        Query: new TuiQueryServices(
            StreamQuery: (text, images, ct) => Compose(ResolveStream()(text, images, ct), ct),
            CreateSession: _ => OnSessionCreated(),
            ExecuteCommand: ExecuteCommandAsync,
            IsImmediateCommand: IsImmediate),
        Session: new TuiSessionServices(
            GetSessionUserPrompts: () => _sessionPrompts,
            GitHelper: _gitHelper),
        Diagnostics: new TuiDiagnosticServices(),
        Runtime: new TuiRuntimeServices(
            GetModel: () => _model,
            // 真实快照 catalog（非替身）：SupportsAttachment / 上下文窗口解析都读它，
            // 换成替身会静默改变多模态门控行为。
            ModelCatalog: ModelCatalogTestHelper.Catalog,
            ModeController: new WorkingModeController(),
            KeyResolver: CreateResolver(),
            KeyContextManager: new KeybindingContextManager
            {
                FocusContext = KeybindingDefaults.ContextChat,
            },
            GetKeybindingWarnings: () => _keybindingWarnings),
        Options: new TuiLaunchOptions(
            Version: "test",
            ExternalCancellation: TestContext.Current.CancellationToken,
            SlashCommands: _slashCommands));

    /// <summary>默认键位解析器，与生产启动时同源（<c>OneCodeToplevel</c> 的兜底分支）。</summary>
    public static KeybindingResolver CreateResolver()
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        return resolver;
    }

    private bool IsImmediate(string input)
    {
        if (!input.StartsWith('/'))
            return false;

        var trimmed = input.TrimStart('/');
        var spaceIndex = trimmed.IndexOf(' ');
        var name = spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
        return _immediateCommands.Contains(name);
    }

    private Task OnSessionCreated()
    {
        foreach (var observer in _sessionCreatedObservers)
            observer();
        return Task.CompletedTask;
    }

    private async Task<string?> ExecuteCommandAsync(string text, CancellationToken ct)
    {
        var result = await (_execute ?? ((_, _) => Task.FromResult<string?>(null)))(text, ct)
            .ConfigureAwait(false);
        foreach (var observer in _commandExecutedObservers)
            observer();
        return result;
    }

    private Func<string, IReadOnlyList<string>?, CancellationToken, IAsyncEnumerable<TuiEvent>> ResolveStream()
        => _stream ?? ((_, _, ct) => ScriptedStream(ct));

    /// <summary>
    /// 包一层以观测「脚本已耗尽」：<c>yield return</c> 之后消费者已同步处理完该事件，
    /// 因此脚本走完即代表全部事件都已落到界面上。
    /// <c>finally</c> 保证取消路径（Esc 打断）同样释放该信号。
    /// </summary>
    private async IAsyncEnumerable<TuiEvent> Compose(
        IAsyncEnumerable<TuiEvent> inner,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var evt in inner.WithCancellation(ct))
                yield return evt;
        }
        finally
        {
            foreach (var observer in _streamExhaustedObservers)
                observer();
        }
    }

    private async IAsyncEnumerable<TuiEvent> ScriptedStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var evt in _script)
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }
}
