using System.Text;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// /design-init - initialize DESIGN.md for the current project.
/// Supports website cloning mode (with URL) and project introspection mode (without URL).
/// </summary>
public sealed class DesignInitCommand(
    IFileSystem fileSystem,
    IConfigManager configManager,
    IPromptManager promptManager,
    ILogger<DesignInitCommand>? logger = null) : Command
{
    public override string Name => "design-init";
    public override string Description => "Initialize DESIGN.md from project assets or by cloning a website's design";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[url] [--force] [--no-llm] [--output <path>]";
    public override string? ProgressMessage => "initializing DESIGN.md";

    private static readonly string[] AllowedTools =
    [
        "Read(*)", "Glob(*)", "Grep(*)",
        "ChromeDevTools(*)", "WebFetch(*)",
        "Write(DESIGN.md)",
    ];

    private static readonly string[] FrontendExtensions =
        [".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".tsx", ".jsx", ".svelte", ".astro"];

    private static readonly Dictionary<string, string> FrameworkMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tailwind.config.js"] = "Tailwind CSS",
        ["tailwind.config.ts"] = "Tailwind CSS",
        ["tailwind.config.cjs"] = "Tailwind CSS",
        ["tailwind.config.mjs"] = "Tailwind CSS",
        ["postcss.config.js"] = "PostCSS",
        ["postcss.config.cjs"] = "PostCSS",
        ["uno.config.ts"] = "UnoCSS",
        ["unocss.config.ts"] = "UnoCSS",
    };

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "target",
        "build", "dist", ".vs", ".idea", ".vscode", "__pycache__",
        ".next", ".nuxt", "coverage", ".cache"
    };

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var cwd = Directory.GetCurrentDirectory();
        var force = args.Contains("--force");
        var noLlm = args.Contains("--no-llm");
        var outputPath = ParseFlag(args, "--output");
        var url = args.FirstOrDefault(a => !a.StartsWith('-') && a != outputPath);

        var designPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(cwd, "DESIGN.md")
            : (Path.IsPathRooted(outputPath) ? outputPath! : Path.Combine(cwd, outputPath!));

        if (File.Exists(designPath) && !force)
            return CommandResult.Text($"DESIGN.md already exists at {designPath}.\nUse /design-init --force to overwrite.");

        if (!string.IsNullOrWhiteSpace(url) && !IsValidUrl(url!))
            return CommandResult.Error($"Invalid URL: {url}\nURL must start with http:// or https:// and point to a public host.");

        if (noLlm || !IsApiKeyAvailable())
        {
            var template = BuildStaticTemplate(url);
            await fileSystem.WriteTextFileAsync(designPath, template, ct).ConfigureAwait(false);
            var suffix = noLlm ? " (static template, --no-llm)" : " (static template, no API key)";
            return CommandResult.Text($"Created DESIGN.md at {designPath}{suffix}");
        }

        var context = await CollectContextAsync(cwd, designPath, url, ct).ConfigureAwait(false);
        var variables = BuildVariables(context);
        var prompt = await LoadPromptAsync(promptManager, "system/design-init", variables, ct).ConfigureAwait(false);
        if (prompt is null)
            return CommandResult.Error("Prompt 'system/design-init' is not available. Verify prompts/system/design-init.prompt exists.");
        return CommandResult.Prompt(prompt, AllowedTools);
    }

    private sealed record DesignInitContext(
        string? Url,
        string WorkingDirectory,
        string FrontendFileSummary,
        string FrameworkHints,
        string? ExistingDesignMd,
        bool ForceOverwrite);

    private async Task<DesignInitContext> CollectContextAsync(
        string cwd, string designPath, string? url, CancellationToken ct)
    {
        var frontendFiles = ScanFrontendFiles(cwd);
        var frameworks = DetectFrameworks(cwd);

        string? existing = null;
        if (File.Exists(designPath))
        {
            existing = await fileSystem.ReadTextFileAsync(designPath, ct).ConfigureAwait(false);
            if (existing is { Length: > 6000 })
                existing = existing[..6000] + "\n... (truncated)";
        }

        return new DesignInitContext(
            Url: url,
            WorkingDirectory: cwd,
            FrontendFileSummary: frontendFiles,
            FrameworkHints: frameworks,
            ExistingDesignMd: existing,
            ForceOverwrite: File.Exists(designPath));
    }

    private static Dictionary<string, string> BuildVariables(DesignInitContext ctx)
    {
        var urlSection = !string.IsNullOrWhiteSpace(ctx.Url)
            ? $"## Target Website (clone mode)\nURL: {ctx.Url}\n\nThe user wants to CLONE the visual design of this website. Use the ChromeDevTools tool (action: screenshot, url: {ctx.Url}, fullPage: true) to capture a screenshot, and the evaluate action to extract computed CSS variables. Then use WebFetch to scrape the page structure.\n\n"
            : "## Target Website\n(not specified - use project introspection mode)\n\n";

        var existingSection = !string.IsNullOrWhiteSpace(ctx.ExistingDesignMd)
            ? $"## Existing DESIGN.md (reference - user requested --force overwrite)\n```markdown\n{ctx.ExistingDesignMd}\n```\n\n"
            : "";

        return new Dictionary<string, string>
        {
            ["url"] = ctx.Url ?? "",
            ["urlSection"] = urlSection,
            ["workingDirectory"] = ctx.WorkingDirectory,
            ["frontendFileSummary"] = ctx.FrontendFileSummary,
            ["frameworkHints"] = ctx.FrameworkHints,
            ["existingDesignMdSection"] = existingSection,
            ["forceOverwrite"] = ctx.ForceOverwrite.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
        };
    }

    private string ScanFrontendFiles(string cwd)
    {
        var sb = new StringBuilder();
        var found = new List<string>();

        try
        {
            ScanDirectory(cwd, cwd, found, depth: 0, maxDepth: 3, maxFiles: 60);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (logger is not null)
                logger.LogDebug(ex, "DesignInitCommand: access denied while scanning {Directory}", cwd);
            else
                System.Diagnostics.Debug.WriteLine($"DesignInitCommand: access denied while scanning {cwd}: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "DesignInitCommand.ScanFrontendFiles failed for {Directory}", cwd);
            else
                System.Diagnostics.Debug.WriteLine($"DesignInitCommand.ScanFrontendFiles failed: {ex.Message}");
        }

        if (found.Count == 0)
        {
            sb.AppendLine("(no frontend files detected - this may be a backend-only project or a new project)");
            return sb.ToString();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Detected {found.Count} frontend file(s):");
        foreach (var file in found)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {file}");
        return sb.ToString();
    }

    private static void ScanDirectory(
        string root, string current, List<string> found, int depth, int maxDepth, int maxFiles)
    {
        if (depth > maxDepth || found.Count >= maxFiles) return;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(current); }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }

        foreach (var entry in entries)
        {
            if (found.Count >= maxFiles) return;

            var name = Path.GetFileName(entry);
            if (ExcludedDirs.Contains(name)) continue;

            if (Directory.Exists(entry))
            {
                ScanDirectory(root, entry, found, depth + 1, maxDepth, maxFiles);
            }
            else
            {
                var ext = Path.GetExtension(entry);
                if (FrontendExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    found.Add(relative);
                }
            }
        }
    }

    private static string DetectFrameworks(string cwd)
    {
        var detected = new List<string>();

        foreach (var (marker, name) in FrameworkMarkers)
        {
            var path = Path.Combine(cwd, marker);
            if (File.Exists(path) && !detected.Contains(name))
                detected.Add(name);
        }

        var pkgPath = Path.Combine(cwd, "package.json");
        if (File.Exists(pkgPath))
        {
            try
            {
                var pkg = File.ReadAllText(pkgPath);
                if (pkg.Contains("\"tailwindcss\"", StringComparison.OrdinalIgnoreCase) && !detected.Contains("Tailwind CSS"))
                    detected.Add("Tailwind CSS");
                if (pkg.Contains("\"@unocss/", StringComparison.OrdinalIgnoreCase) && !detected.Contains("UnoCSS"))
                    detected.Add("UnoCSS");
                if (pkg.Contains("\"styled-components\"", StringComparison.OrdinalIgnoreCase) && !detected.Contains("styled-components"))
                    detected.Add("styled-components");
                if (pkg.Contains("\"@emotion/", StringComparison.OrdinalIgnoreCase) && !detected.Contains("Emotion"))
                    detected.Add("Emotion");
                if (pkg.Contains("\"antd\"", StringComparison.OrdinalIgnoreCase) && !detected.Contains("Ant Design"))
                    detected.Add("Ant Design");
                if (pkg.Contains("\"@chakra-ui/", StringComparison.OrdinalIgnoreCase) && !detected.Contains("Chakra UI"))
                    detected.Add("Chakra UI");
                if (pkg.Contains("\"@mui/material\"", StringComparison.OrdinalIgnoreCase) && !detected.Contains("MUI"))
                    detected.Add("MUI");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DesignInitCommand.DetectFrameworks package.json read failed: {ex.Message}");
            }
        }

        return detected.Count == 0
            ? "(no CSS framework detected - plain CSS or custom setup)"
            : string.Join(", ", detected);
    }

    private static string BuildStaticTemplate(string? url)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("name: Project Design System");
        sb.AppendLine("description: >");
        sb.AppendLine("  Design system for this project. Edit this file to capture colors,");
        sb.AppendLine("  typography, spacing, components, and design guidelines.");
        if (!string.IsNullOrWhiteSpace(url))
        {
            sb.AppendLine("  Initially scaffolded to clone the visual style of:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {url}");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Design System");
        sb.AppendLine();
        sb.AppendLine("> This file follows the DESIGN.md format (https://designmd.ai/what-is-design-md).");
        sb.AppendLine("> Drop it into your project root and AI coding agents will follow these guidelines");
        sb.AppendLine("> when working on UI/design/frontend tasks.");
        sb.AppendLine();
        sb.AppendLine("## Colors");
        sb.AppendLine();
        sb.AppendLine("### Primary");
        sb.AppendLine("- **primary** (`#6366F1`): Main brand color for buttons and links");
        sb.AppendLine();
        sb.AppendLine("### Backgrounds");
        sb.AppendLine("- **bg-root** (`#FFFFFF`): Page background");
        sb.AppendLine("- **bg-surface** (`#F8FAFC`): Cards and elevated surfaces");
        sb.AppendLine("- **bg-elevated** (`#F1F5F9`): Overlays and popups");
        sb.AppendLine();
        sb.AppendLine("### Semantic");
        sb.AppendLine("- **success** (`#22C55E`): Completed states, confirmations");
        sb.AppendLine("- **warning** (`#F59E0B`): Pending actions, cautions");
        sb.AppendLine("- **error** (`#EF4444`): Failures, destructive actions");
        sb.AppendLine("- **info** (`#3B82F6`): Informational highlights");
        sb.AppendLine();
        sb.AppendLine("### Text");
        sb.AppendLine("- **text-primary** (`#0F172A`): Main text color");
        sb.AppendLine("- **text-secondary** (`#475569`): Descriptions, secondary text");
        sb.AppendLine("- **text-muted** (`#94A3B8`): Placeholders, disabled states");
        sb.AppendLine();
        sb.AppendLine("### Borders");
        sb.AppendLine("- **border** (`#E2E8F0`): Default borders");
        sb.AppendLine("- **border-active** (`#6366F1`): Focused / active borders");
        sb.AppendLine();
        sb.AppendLine("## Typography");
        sb.AppendLine();
        sb.AppendLine("| Token | Font | Size | Weight | Usage |");
        sb.AppendLine("|-------|------|------|--------|-------|");
        sb.AppendLine("| `heading` | Inter, system-ui, sans-serif | 32px | bold | Page titles |");
        sb.AppendLine("| `subheading` | Inter, system-ui, sans-serif | 20px | semibold | Section headers |");
        sb.AppendLine("| `body` | Inter, system-ui, sans-serif | 16px | regular | Body text |");
        sb.AppendLine("| `caption` | Inter, system-ui, sans-serif | 13px | regular | Captions, metadata |");
        sb.AppendLine();
        sb.AppendLine("## Spacing");
        sb.AppendLine();
        sb.AppendLine("| Token | Value | Usage |");
        sb.AppendLine("|-------|-------|-------|");
        sb.AppendLine("| `xs` | 4px | Tight gaps, icon padding |");
        sb.AppendLine("| `sm` | 8px | Input padding, list gaps |");
        sb.AppendLine("| `md` | 12px | Card padding, component gaps |");
        sb.AppendLine("| `lg` | 16px | Section padding |");
        sb.AppendLine("| `xl` | 24px | Page-level spacing |");
        sb.AppendLine("| `2xl` | 32px | Major section breaks |");
        sb.AppendLine();
        sb.AppendLine("## Components");
        sb.AppendLine();
        sb.AppendLine("### Buttons");
        sb.AppendLine("- **Primary**: bg `primary`, text white, radius 8px, padding 8px 16px");
        sb.AppendLine("- **Secondary**: bg `bg-surface`, text `text-primary`, border 1px `border`, radius 8px");
        sb.AppendLine("- **Ghost**: transparent bg, text `primary`, no border");
        sb.AppendLine("- **Danger**: bg `error`, text white, radius 8px");
        sb.AppendLine();
        sb.AppendLine("### Cards");
        sb.AppendLine("- Background: `bg-surface`");
        sb.AppendLine("- Border: 1px solid `border`");
        sb.AppendLine("- Radius: 12px");
        sb.AppendLine("- Padding: 16px (`lg`)");
        sb.AppendLine();
        sb.AppendLine("### Inputs");
        sb.AppendLine("- Background: `bg-root`");
        sb.AppendLine("- Border: 1px solid `border`, focuses to `border-active`");
        sb.AppendLine("- Radius: 8px");
        sb.AppendLine("- Padding: 8px 12px");
        sb.AppendLine();
        sb.AppendLine("## Elevation");
        sb.AppendLine();
        sb.AppendLine("| Level | Shadow | Usage |");
        sb.AppendLine("|-------|--------|-------|");
        sb.AppendLine("| `sm` | `0 1px 2px rgba(0,0,0,0.05)` | Cards, default |");
        sb.AppendLine("| `md` | `0 4px 6px rgba(0,0,0,0.1)` | Hovered cards, dropdowns |");
        sb.AppendLine("| `lg` | `0 10px 15px rgba(0,0,0,0.1)` | Modals, popovers |");
        sb.AppendLine();
        sb.AppendLine("## Guidelines");
        sb.AppendLine();
        sb.AppendLine("### Do's");
        sb.AppendLine("- Use the semantic color tokens (`primary`, `success`, `warning`, `error`) - never hard-code hex values.");
        sb.AppendLine("- Reference spacing via the token scale (`xs`/`sm`/`md`/`lg`/`xl`).");
        sb.AppendLine("- Keep radius consistent: 8px for inputs/buttons, 12px for cards.");
        sb.AppendLine("- Use `text-primary` for body text; reserve `text-muted` for de-emphasized content.");
        sb.AppendLine();
        sb.AppendLine("### Don'ts");
        sb.AppendLine("- Don't introduce new colors outside this palette without adding a token here first.");
        sb.AppendLine("- Don't mix font families - stick to Inter (or the configured stack).");
        sb.AppendLine("- Don't use box-shadows beyond the three elevation levels.");
        sb.AppendLine("- Don't use pure black (`#000000`) for text - use `text-primary` instead.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(url))
        {
            sb.AppendLine("## Reference");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"This design system was scaffolded to clone the visual style of: {url}");
            sb.AppendLine("Re-run `/design-init --force` to regenerate, or edit this file directly to refine.");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private bool IsApiKeyAvailable() =>
        !string.IsNullOrEmpty(configManager.Current.Effective.ApiKey);

    private static bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.Length > 2000) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
