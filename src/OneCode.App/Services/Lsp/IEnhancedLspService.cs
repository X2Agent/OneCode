namespace OneCode.App.Services.Lsp;

/// <summary>
/// Abstraction over <see cref="EnhancedLspService"/> for LSP notification consumers
/// (LspNotifier). 存在目的：诊断新鲜度判定（P2）需要可在单测中伪造服务器状态与
/// didChange 基线，具体类无法替换（对齐 IMcpConnectionManager 先例）。
/// </summary>
public interface IEnhancedLspService
{
    Task NotifyFileUpdatedAsync(string filePath, CancellationToken ct = default);
    Task NotifyFileClosedAsync(string filePath, CancellationToken ct = default);
    Task NotifyDirectoryDeletedAsync(string directoryPath, CancellationToken ct = default);

    /// <summary>是否有任何 LSP 服务器处于运行中。诊断等待的短路依据：无服务器时永远等不到新诊断。</summary>
    bool HasRunningServer { get; }

    /// <summary>是否有运行中的服务器正在索引（$/progress 进行中）——诊断等待预算自适应延长的依据。</summary>
    bool IsIndexing { get; }

    /// <summary>指定文件最近一次 didChange/didOpen 的发送时刻（诊断新鲜度基线）；未推送过返回 null。</summary>
    DateTimeOffset? GetLastDidChangeUtc(string filePath);
}