namespace OneCode.Infrastructure.Config;

public static partial class Constants
{
    /// <summary>
    /// LSP subsystem constants — timeouts, thresholds, and limits shared across
    /// LspClient, LspServerManager, LspNotifier, tools, and installers.
    /// </summary>
    public static class Lsp
    {
        // LspClient
        public const int RequestTimeoutSec = 30;
        // csharp-ls / rust-analyzer may load a full MSBuild/Cargo workspace during initialize
        public const int InitializeTimeoutSec = 120;
        public const int ProcessExitWaitMs = 5000;
        public const int OutstandingRequestDrainSec = 3;

        // LspServerManager
        public static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

        // LspServerManager — diagnostics cleanup
        public static readonly TimeSpan DiagnosticsMaxAge = TimeSpan.FromHours(1);
        public static readonly TimeSpan DiagnosticsCleanupInterval = TimeSpan.FromMinutes(5);

        // LspServerManager — 通知发送超时：stdin 管道直写（LspProtocol.WriteFrameAsync）无内建
        // 超时，服务器假死（活着但不读 stdin、缓冲区写满）会永久阻塞 didOpen/didChange/didClose
        // 广播——而广播在 Write/Edit/Delete 完成路径上同步 await。超时即标记 unhealthy，
        // 由崩溃自愈循环（指数退避重启）接管。
        public static readonly TimeSpan NotificationSendTimeout = TimeSpan.FromSeconds(2);

        // LspNotifier
        public const int MaxDiagnosticsInSummary = 5;

        // LspNotifier — didChange 后诊断发布等待的轮询节奏常量已随实现移至
        // LspNotifier（internal static，同 McpConnectionManager.AutoReconnectInterval 先例，
        // 供测试注入加速轮询）。

        // LanguagePackInstaller
        public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan PrereqCheckTimeout = TimeSpan.FromSeconds(10);

        // LspTool
        public const int DefaultTabSize = 4;

        // SymbolSearchTool
        public const int MaxResultsUpper = 100;

        // FindReferencesTool
        public const int DeclarationSearchMax = 5;
        public const int RipgrepMaxColumns = 500;
    }
}
