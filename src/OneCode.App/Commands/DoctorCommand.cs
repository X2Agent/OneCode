using System.Text;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Mcp;
using OneCode.Core.Lsp;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Commands;

/// <summary>
/// /doctor — environment &amp; configuration diagnostics.
///
/// Subcommands:
///   /doctor         → full diagnostics report (default)
///   /doctor info    → same as default
///   /doctor env     → relevant environment variables
///   /doctor setup   → first-time setup checklist
///
/// Checks real failure modes users hit:
///   • API key resolution (settings.json → env var, with provider-aware "required" check)
///   • settings.json parse health (file exists? syntax valid?)
///   • MCP server connectivity (configured vs connected, tool count per server)
///   • LSP server state (running vs initialised, per server)
///   • Git availability (needed by /commit, /review, worktree-based skills)
///
/// Deliberately NOT checked (low signal):
///   • Config dir existence — auto-created on first run; if missing the app wouldn't start
///   • Runtime / OS / Version — always ✓, not diagnostic. See /status or /version instead.
/// </summary>
public sealed class DoctorCommand(
    IConfigManager configManager,
    IGitHelper gitHelper,
    IFileSystem fileSystem,
    IMcpConnectionManager mcpConnectionManager,
    ILspServerManager lspServerManager,
    ILogger<DoctorCommand>? logger = null) : Command
{
    public override string Name => "doctor";
    public override string Description => "Diagnose environment, config, MCP & LSP health";
    public override CommandCategory Category => CommandCategory.Diagnostic;
    public override string? ArgumentHint => "[info|env|setup]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
        return sub switch
        {
            "info" => await ShowInfoAsync(ct).ConfigureAwait(false),
            "env" => ShowEnv(),
            "setup" => await ShowSetupAsync(ct).ConfigureAwait(false),
            _ => CommandResult.Error($"Unknown subcommand: {sub}. Use: info, env, setup"),
        };
    }

    // /doctor info — full diagnostics

    private async Task<CommandResult> ShowInfoAsync(CancellationToken ct)
    {
        var sb = new StringBuilder("Doctor — Diagnostics:");
        sb.AppendLine();

        // 1. API key（来源由 ConfigManager 统一解析）
        var settings = configManager.Current.Effective;
        var provider = string.IsNullOrEmpty(settings.Provider)
            ? CoreConstants.ModelProviders.Anthropic
            : settings.Provider.ToLowerInvariant();
        var requiresAuth = !string.Equals(provider, CoreConstants.ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase);

        var apiKey = settings.ApiKey;
        var apiKeyInfo = configManager.Current.GetValueInfo(CoreConstants.ConfigKeys.ApiKey);
        var apiKeySource = apiKey is null ? null : apiKeyInfo.Source.ToString().ToLowerInvariant();

        if (requiresAuth)
        {
            var ok = !string.IsNullOrEmpty(apiKey);
            var detail = ok
                ? $"(from {apiKeySource})"
                : "(not set — set via /config or ONECODE_API_KEY env var)";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {(ok ? "✓" : "✗")} API Key: {detail}");
        }
        else
        {
            // Ollama doesn't need an API key — don't report a false negative.
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ✓ API Key: not required (provider={provider})");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"  · Provider: {provider}");

        // 2. settings.json health
        AppendSettingsHealth(sb);

        // 3. MCP servers
        AppendMcpStatus(sb);

        // 4. LSP servers
        AppendLspStatus(sb);

        // 5. Git
        var gitVersion = await gitHelper.GetVersionAsync(ct).ConfigureAwait(false);
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {(gitVersion is not null ? "✓" : "✗")} Git: {gitVersion ?? "(not found — needed by /commit, /review, /batch)"}");

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private void AppendSettingsHealth(StringBuilder sb)
    {
        var globalPath = configManager.SettingsFilePath;
        var projectPath = configManager.ProjectSettingsFilePath;

        var globalExists = File.Exists(globalPath);
        var projectExists = projectPath is not null && File.Exists(projectPath);

        // Parse validity: ConfigManager.Load() swallows parse errors into a Console.Error
        // warning and falls back to empty AppSettings. We can't directly detect the error
        // from here, but if the file exists yet Settings.ApiKey/Provider/Model are ALL null/empty,
        // that's a strong signal the file failed to parse (or is empty).
        var globalParseOk = true;
        if (globalExists)
        {
            globalParseOk = TryValidateSettingsFile(globalPath, out var parseError);
            if (!globalParseOk)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ✗ Global settings: {globalPath}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"           → parse error: {parseError}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ✓ Global settings: {globalPath}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  · Global settings: not found ({globalPath})");
        }

        if (projectPath is not null)
        {
            if (projectExists)
            {
                var projectParseOk = TryValidateSettingsFile(projectPath, out var parseError);
                if (!projectParseOk)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  ✗ Project settings: {projectPath}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"           → parse error: {parseError}");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  ✓ Project settings: {projectPath}");
                }
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  · Project settings: not found ({projectPath})");
            }
        }
    }

    /// <summary>
    /// Attempts to parse a settings.json file to detect syntax errors early.
    /// Returns false if the file exists but contains invalid JSON.
    /// </summary>
    private static bool TryValidateSettingsFile(string path, out string? error)
    {
        try
        {
            var content = File.ReadAllText(path);
            _ = System.Text.Json.JsonDocument.Parse(content);
            error = null;
            return true;
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void AppendMcpStatus(StringBuilder sb)
    {
        IReadOnlyList<McpServerStatus> statuses;
        try
        {
            statuses = mcpConnectionManager.GetStatus();
        }
        catch (Exception ex)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ✗ MCP: status query failed ({ex.Message})");
            return;
        }

        if (statuses.Count == 0)
        {
            sb.AppendLine("  · MCP: no servers configured");
            return;
        }

        var connected = statuses.Count(s => s.IsConnected);
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {(connected == statuses.Count ? "✓" : connected == 0 ? "✗" : "○")} MCP: {connected}/{statuses.Count} servers connected");

        foreach (var s in statuses)
        {
            var mark = s.IsConnected ? "✓" : "✗";
            var tools = s.IsConnected ? $", {s.ToolCount} tools" : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"      {mark} {s.Name}{tools}");
        }
    }

    private void AppendLspStatus(StringBuilder sb)
    {
        IReadOnlyList<LspServerStatus> statuses;
        try
        {
            statuses = lspServerManager.GetStatus();
        }
        catch (Exception ex)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ✗ LSP: status query failed ({ex.Message})");
            return;
        }

        if (statuses.Count == 0)
        {
            sb.AppendLine("  · LSP: no servers configured");
            return;
        }

        var running = statuses.Count(s => s.IsRunning);
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {(running == statuses.Count ? "✓" : running == 0 ? "✗" : "○")} LSP: {running}/{statuses.Count} servers running");

        foreach (var s in statuses)
        {
            var mark = s.IsRunning ? "✓" : "✗";
            var init = s.IsInitialized ? "" : " (not initialised)";
            sb.AppendLine(CultureInfo.InvariantCulture, $"      {mark} {s.Name}{init}");
        }
    }

    // /doctor env

    private static CommandResult ShowEnv()
    {
        // Only OneCode-relevant env vars (was: generic HOME/PATH/OS/USER/SHELL which
        // carry no diagnostic value). Proxy vars included because they frequently break
        // API connectivity in corporate environments.
        var relevantVars = new (string Name, string? Description)[]
        {
            (CoreConstants.EnvVars.OneCodeApiKey, "API key (overrides settings.json)"),
            (CoreConstants.EnvVars.OneCodeBaseUrl, "API base URL override"),
            (CoreConstants.EnvVars.OneCodeModel, "Default model override"),
            (CoreConstants.EnvVars.OneCodeWebSearchProvider, "Web search provider"),
            (CoreConstants.EnvVars.OneCodeWebSearchApiKey, "Web search API key"),
            (CoreConstants.EnvVars.BraveSearchApiKey, "Brave Search API key"),
            (CoreConstants.EnvVars.HttpProxy, "HTTP proxy"),
            (CoreConstants.EnvVars.HttpsProxy, "HTTPS proxy"),
            (CoreConstants.EnvVars.NoProxy, "No-proxy bypass list"),
            (CoreConstants.EnvVars.Vcr, "VCR record/replay mode"),
        };

        var sb = new StringBuilder("Environment variables (OneCode-relevant):");
        foreach (var (name, desc) in relevantVars)
        {
            var val = Environment.GetEnvironmentVariable(name);
            var display = IsSecret(name)
                ? (string.IsNullOrEmpty(val) ? "(not set)" : "(set, hidden)")
                : (val ?? "(not set)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {name,-32} {display}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {"",-32} {desc}");
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private static bool IsSecret(string name) =>
        name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Token", StringComparison.OrdinalIgnoreCase);

    // /doctor setup — first-time checklist

    private async Task<CommandResult> ShowSetupAsync(CancellationToken ct)
    {
        var hasGit = await gitHelper.GetVersionAsync(ct).ConfigureAwait(false) is not null;

        var hasMd = await fileSystem.ReadTextFileAsync(
            Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md"), ct)
            .ConfigureAwait(false) is not null;

        var settings = configManager.Current.Effective;
        var provider = string.IsNullOrEmpty(settings.Provider)
            ? CoreConstants.ModelProviders.Anthropic
            : settings.Provider.ToLowerInvariant();
        var requiresAuth = !string.Equals(provider, CoreConstants.ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase);
        var hasApiKey = !requiresAuth || !string.IsNullOrEmpty(settings.ApiKey);

        var steps = new (string Title, string Fix, bool Done)[]
        {
            ("API Key configured",
             $"Run /config to set it (or export {CoreConstants.EnvVars.OneCodeApiKey} env var)",
             hasApiKey),

            ("Git available",
             "Install Git from https://git-scm.com/",
             hasGit),

            ("AGENTS.md exists",
             "Run /init to create one for this project",
             hasMd),
        };

        var sb = new StringBuilder();
        sb.AppendLine("Setup Checklist:");
        sb.AppendLine();

        var allDone = true;
        for (var i = 0; i < steps.Length; i++)
        {
            var (title, fix, done) = steps[i];
            allDone &= done;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {(done ? "✓" : "○")} Step {i + 1}: {title}");
            if (!done) sb.AppendLine(CultureInfo.InvariantCulture, $"           → {fix}");
        }

        sb.AppendLine();
        sb.AppendLine(allDone
            ? "All set! Type your first question to get started."
            : "Complete the steps above, then run /doctor setup again.");

        return CommandResult.Text(sb.ToString().TrimEnd());
    }
}
