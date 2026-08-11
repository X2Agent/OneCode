using OneCode.App.Session;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services;

/// <summary>
/// Adapts <see cref="ISessionManager"/> (App layer) to the
/// <see cref="IWorkingDirectoryAccessor"/> contract (Core layer), and also exposes
/// the additional directories registered via <c>/add-dir</c> / <c>--add-dir</c>
/// (stored in <see cref="AppSettings.AllowedDirectories"/>).
/// </summary>
/// <remarks>
/// Core-layer tools cannot depend on <see cref="ISessionManager"/> (App) or
/// <see cref="IConfigManager"/> (Infrastructure), so this adapter lives in App
/// and bridges the dependency inversion. It also breaks what would otherwise be
/// a DI cycle: SessionManager → IPlanFileService → IWorkingDirectoryAccessor → SessionManager.
/// </remarks>
internal sealed class SessionWorkingDirectoryAccessor : IWorkingDirectoryAccessor
{
    private readonly ISessionWorkingDirectory _sessionManager;
    private readonly IConfigManager _configManager;

    public SessionWorkingDirectoryAccessor(ISessionWorkingDirectory sessionManager, IConfigManager configManager)
    {
        _sessionManager = sessionManager;
        _configManager = configManager;
    }

    public string WorkingDirectory => _sessionManager.WorkingDirectory;

    public IReadOnlyList<string> AdditionalDirectories
    {
        get
        {
            var dirs = _configManager.Current.Effective.AllowedDirectories;
            if (dirs.Count == 0)
                return Array.Empty<string>();

            // Defensive snapshot — AllowedDirectories is a mutable List<string>;
            // return an isolated copy so external mutation can't affect callers.
            return dirs.ToArray();
        }
    }
}
