using OneCode.Core.Prompt;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 模式运行时实现：会话模式、提示词缓存与权限 provider 同步。
/// </summary>
public sealed class PlanModeService : IPlanModeService
{
    private readonly IPermissionModeProvider _permissionModeProvider;
    private readonly IPromptManager _promptManager;
    private readonly Lock _lock = new();
    private string? _cachedPlanPrompt;

    private bool _inPlanMode;
    private PermissionMode? _prePlanMode;

    public PlanModeService(
        IPermissionModeProvider permissionModeProvider,
        IPromptManager promptManager)
    {
        _permissionModeProvider = permissionModeProvider;
        _promptManager = promptManager;
    }

    public bool IsInPlanMode
    {
        get { lock (_lock) return _inPlanMode; }
    }

    public PermissionMode? PrePlanMode
    {
        get { lock (_lock) return _prePlanMode; }
    }

    /// <inheritdoc />
    public void EnterPlanMode()
    {
        lock (_lock)
        {
            if (_inPlanMode)
                return;
            _prePlanMode = _permissionModeProvider.CurrentMode;
            _inPlanMode = true;
        }

        _permissionModeProvider.SetCurrentMode(PermissionMode.Plan);
    }

    /// <inheritdoc />
    public PermissionMode ExitPlanMode()
    {
        PermissionMode restored;
        lock (_lock)
        {
            if (!_inPlanMode)
                return _permissionModeProvider.CurrentMode;
            restored = _prePlanMode ?? PermissionMode.Default;
            _inPlanMode = false;
            _prePlanMode = null;
        }

        _permissionModeProvider.SetCurrentMode(restored);
        return restored;
    }

    public async Task<string> GetWorkflowInstructionsAsync(CancellationToken ct)
    {
        if (_cachedPlanPrompt is not null) return _cachedPlanPrompt;

        var prompt = await _promptManager.GetPromptAsync("system/plan", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Plan prompt 'system/plan' not found in any IPromptManager store.");
        _cachedPlanPrompt = prompt;
        return _cachedPlanPrompt;
    }
}
