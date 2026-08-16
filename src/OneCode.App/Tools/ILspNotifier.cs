namespace OneCode.App.Tools;

/// <summary>
/// Notifies an editor host that a file changed on disk.
/// The production DI registers <see cref="LspNotifier"/>; <see cref="NoOpLspNotifier"/>
/// is available for tests and environments without an LSP server.
/// </summary>
public interface ILspNotifier
{
    Task NotifyFileUpdatedAsync(string fullPath, CancellationToken ct = default);

    /// <summary>
    /// Get a brief diagnostics summary for a file after LSP has processed the change.
    /// Returns null if no LSP server is running or no diagnostics exist for the file.
    /// The implementation may wait briefly for the server to publish fresh diagnostics.
    /// </summary>
    Task<string?> GetDiagnosticsSummaryAsync(string fullPath, CancellationToken ct = default);
}

public sealed class NoOpLspNotifier : ILspNotifier
{
    public static readonly NoOpLspNotifier Instance = new();

    private NoOpLspNotifier()
    {
    }

    public Task NotifyFileUpdatedAsync(string fullPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string?> GetDiagnosticsSummaryAsync(string fullPath, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// Post-write pipeline shared by EditTool and WriteTool.
/// Handles LSP notification → diagnostics collection.
/// </summary>
public static class FileWritePipeline
{
    public static async Task<string> CompleteWriteAsync(
        string fullPath,
        string content,
        ILspNotifier notifier,
        string successMessage,
        CancellationToken ct)
    {
        await notifier.NotifyFileUpdatedAsync(fullPath, ct).ConfigureAwait(false);
        var diagSummary = await notifier.GetDiagnosticsSummaryAsync(fullPath, ct).ConfigureAwait(false);
        return diagSummary is null ? successMessage : $"{successMessage}\n\n{diagSummary}";
    }
}
