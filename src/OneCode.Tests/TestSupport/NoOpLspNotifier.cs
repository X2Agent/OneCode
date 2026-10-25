using OneCode.App.Tools;

namespace OneCode.Tests;

/// <summary>
/// No-op <see cref="ILspNotifier"/>：测试环境中无 LSP 服务器时使用，
/// 所有通知方法直接完成、诊断摘要恒为 null。
/// </summary>
public sealed class NoOpLspNotifier : ILspNotifier
{
    public static readonly NoOpLspNotifier Instance = new();

    private NoOpLspNotifier()
    {
    }

    public Task NotifyFileUpdatedAsync(string fullPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyFileClosedAsync(string fullPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyDirectoryDeletedAsync(string directoryPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string?> GetDiagnosticsSummaryAsync(string fullPath, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
