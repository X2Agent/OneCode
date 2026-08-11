namespace OneCode.Core.Permissions.Yolo;

/// <summary>
/// Abstraction for loading/saving YOLO rule files, allowing the Automation layer
/// to depend on this Core interface rather than the Infrastructure implementation.
/// </summary>
public interface IYoloRuleFileStore
{
    /// <summary>Path to the rules file on disk.</summary>
    string RulesPath { get; }

    /// <summary>
    /// Load user rules, or built-in defaults when missing/empty/unreadable.
    /// </summary>
    Task<IReadOnlyList<UserRule>> LoadOrDefaultsAsync(CancellationToken ct = default);
}
