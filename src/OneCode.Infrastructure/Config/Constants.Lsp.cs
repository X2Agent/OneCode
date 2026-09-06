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

        // LspNotifier
        public const int MaxDiagnosticsInSummary = 5;

        // LspNotifier — didChange 后诊断发布等待：轮询新诊断而非单次 sleep，
        // 大项目首次分析远超固定 settle 时间，假阴性会误导模型。
        public const int DiagnosticsPollIntervalMs = 200;
        public const int DiagnosticsWaitTotalMs = 2000;

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
