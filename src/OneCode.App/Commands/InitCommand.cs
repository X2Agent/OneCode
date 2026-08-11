using System.Text;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Tools;

namespace OneCode.App.Commands;

/// <summary>
/// /init — initialize AGENTS.md for the current project.
///
/// When an API key is configured, collects project context (project type, README,
/// directory structure, marker files) and delegates AGENTS.md authoring to the LLM
/// via a Prompt result. Prompt loaded from prompts/system/init.prompt (overridable
/// via project/user-level .onecode/prompts/).
///
/// When no API key is available, or when --no-llm is passed, falls back to a static
/// template generated from project type detection (ProjectCommandDetector).
///
/// Usage:
///   /init              → LLM-generated AGENTS.md (or static fallback if no API key)
///   /init --force      → overwrite an existing AGENTS.md
///   /init --no-llm     → force static template, bypass LLM even if API key is set
/// </summary>
public sealed class InitCommand(
    IFileSystem fileSystem,
    IConfigManager configManager,
    IPromptManager promptManager,
    ILogger<InitCommand>? logger = null) : Command
{
    public override string Name => "init";
    public override string Description => "Initialize AGENTS.md for the current project";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[--force] [--no-llm]";
    public override string? ProgressMessage => "initializing AGENTS.md";

    private static readonly string[] AllowedTools =
        ["Read(*)", "Glob(*)", "Grep(*)", "Write(AGENTS.md)"];

    private static readonly Dictionary<string, (string Build, string Test)> ProjectCommands = new()
    {
        ["dotnet"] = ("dotnet build", "dotnet test"),
        ["node"] = ("npm run build", "npm test"),
        ["rust"] = ("cargo build", "cargo test"),
        ["go"] = ("go build ./...", "go test ./..."),
        ["python"] = ("pip install -e .", "pytest"),
        ["java"] = ("mvn compile", "mvn test"),
    };

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var cwd = Directory.GetCurrentDirectory();
        var mdPath = Path.Combine(cwd, "AGENTS.md");
        var force = args.Contains("--force");
        var noLlm = args.Contains("--no-llm");

        if (File.Exists(mdPath) && !force)
            return CommandResult.Text($"AGENTS.md already exists at {mdPath}.\nUse /init --force to overwrite.");

        // 无 API Key 或显式 --no-llm → 静态模板回退
        if (noLlm || !IsApiKeyAvailable())
        {
            var template = BuildStaticTemplate(cwd);
            await fileSystem.WriteTextFileAsync(mdPath, template, ct).ConfigureAwait(false);
            var suffix = noLlm ? " (static template, --no-llm)" : " (static template, no API key)";
            return CommandResult.Text($"Created AGENTS.md at {mdPath}{suffix}");
        }

        // LLM 路径：采集上下文 + 构建 Prompt
        var context = await CollectContextAsync(cwd, mdPath, ct).ConfigureAwait(false);
        var variables = BuildVariables(context);
        var prompt = await LoadPromptAsync(promptManager, "system/init", variables, ct).ConfigureAwait(false);
        if (prompt is null)
            return CommandResult.Error("Prompt 'system/init' is not available. Verify prompts/system/init.prompt exists.");
        return CommandResult.Prompt(prompt, AllowedTools);
    }

    private bool IsApiKeyAvailable() =>
        !string.IsNullOrEmpty(configManager.Current.Effective.ApiKey);

    private static Dictionary<string, string> BuildVariables(InitContext ctx)
    {
        var buildLine = !string.IsNullOrEmpty(ctx.BuildCommand)
            ? $"- **Build command:** `{ctx.BuildCommand}`\n"
            : "";
        var testLine = !string.IsNullOrEmpty(ctx.TestCommand)
            ? $"- **Test command:** `{ctx.TestCommand}`\n"
            : "";

        var markerSection = !string.IsNullOrEmpty(ctx.MarkerDetails)
            ? $"## Project Marker File\n```\n{ctx.MarkerDetails}\n```\n\n"
            : "";

        var existingSection = !string.IsNullOrEmpty(ctx.ExistingAgentsMd)
            ? $"## Existing AGENTS.md (reference — user requested --force overwrite)\n```markdown\n{ctx.ExistingAgentsMd}\n```\n\n"
            : "";

        return new Dictionary<string, string>
        {
            ["projectType"] = ctx.ProjectType,
            ["buildCommand"] = ctx.BuildCommand,
            ["testCommand"] = ctx.TestCommand,
            ["buildCommandLine"] = buildLine,
            ["testCommandLine"] = testLine,
            ["readmeContent"] = ctx.ReadmeContent ?? "(no README found)",
            ["directoryTree"] = ctx.DirectoryTree,
            ["markerSection"] = markerSection,
            ["existingAgentsMdSection"] = existingSection,
        };
    }

    // 上下文采集

    private sealed record InitContext(
        string ProjectType,
        string BuildCommand,
        string TestCommand,
        string? ReadmeContent,
        string DirectoryTree,
        string? MarkerDetails,
        string? ExistingAgentsMd);

    private async Task<InitContext> CollectContextAsync(string cwd, string mdPath, CancellationToken ct)
    {
        var projectType = DetectProjectType(cwd);
        var (build, test) = ProjectCommands.TryGetValue(projectType, out var cmds) ? cmds : ("", "");

        var readme = await TryReadReadmeAsync(cwd, ct).ConfigureAwait(false);
        var tree = BuildDirectoryTree(cwd);
        var markerDetails = await TryReadMarkerDetailsAsync(cwd, projectType, ct).ConfigureAwait(false);

        string? existing = null;
        if (File.Exists(mdPath))
        {
            existing = await fileSystem.ReadTextFileAsync(mdPath, ct).ConfigureAwait(false);
            if (existing is { Length: > 6000 })
                existing = existing[..6000] + "\n... (truncated)";
        }

        return new InitContext(projectType, build, test, readme, tree, markerDetails, existing);
    }

    private static string DetectProjectType(string cwd)
    {
        if (ProjectCommandDetector.HasMarker(cwd, "*.csproj", "*.vbproj", "*.fsproj", "*.slnx", "*.sln"))
            return "dotnet";
        if (ProjectCommandDetector.HasMarker(cwd, "package.json")) return "node";
        if (ProjectCommandDetector.HasMarker(cwd, "Cargo.toml")) return "rust";
        if (ProjectCommandDetector.HasMarker(cwd, "go.mod")) return "go";
        if (ProjectCommandDetector.HasMarker(cwd, "pyproject.toml", "pytest.ini")) return "python";
        if (ProjectCommandDetector.HasMarker(cwd, "pom.xml", "build.gradle", "build.gradle.kts")) return "java";
        return "unknown";
    }

    private async Task<string?> TryReadReadmeAsync(string cwd, CancellationToken ct)
    {
        var candidates = new[] { "README.md", "README_CN.md", "README.MD", "readme.md" };
        foreach (var name in candidates)
        {
            var path = Path.Combine(cwd, name);
            var content = await fileSystem.ReadTextFileAsync(path, ct).ConfigureAwait(false);
            if (content is { Length: > 0 })
            {
                if (content.Length > 4000)
                    content = content[..4000] + "\n... (truncated)";
                return content;
            }
        }
        return null;
    }

    private string BuildDirectoryTree(string cwd)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".svn", ".hg", "node_modules", "bin", "obj", "target",
            "build", "dist", ".vs", ".idea", ".vscode", "__pycache__",
            ".next", ".nuxt", "coverage", ".cache"
        };

        var sb = new StringBuilder();
        try
        {
            var entries = Directory.GetFileSystemEntries(cwd)
                .Select(Path.GetFileName)
                .Where(n => !excluded.Contains(n!))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();

            foreach (var entry in entries)
            {
                var full = Path.Combine(cwd, entry!);
                var isDir = Directory.Exists(full);
                var entryLine = isDir ? $"  {entry}/" : $"  {entry}";
                sb.Append(entryLine).AppendLine();

                if (!isDir) continue;
                try
                {
                    var children = Directory.GetFileSystemEntries(full)
                        .Select(Path.GetFileName)
                        .Where(n => !excluded.Contains(n!))
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Take(15)
                        .ToList();
                    foreach (var child in children)
                    {
                        var childFull = Path.Combine(full, child!);
                        var childLine = Directory.Exists(childFull) ? $"    {child}/" : $"    {child}";
                        sb.Append(childLine).AppendLine();
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (logger is not null)
                        logger.LogDebug(ex, "InitCommand: access denied while scanning {Directory}", full);
                    else
                        System.Diagnostics.Debug.WriteLine($"InitCommand: access denied while scanning {full}: {ex.Message}");
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            if (logger is not null)
                logger.LogDebug(ex, "InitCommand: access denied while scanning {Directory}", cwd);
            else
                System.Diagnostics.Debug.WriteLine($"InitCommand: access denied while scanning {cwd}: {ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            // 测试或 cwd 已被删除时降级为空树，不阻断 /init LLM 路径
            if (logger is not null)
                logger.LogDebug(ex, "InitCommand: directory not found while scanning {Directory}", cwd);
            else
                System.Diagnostics.Debug.WriteLine($"InitCommand: directory not found while scanning {cwd}: {ex.Message}");
        }

        return sb.ToString();
    }

    private async Task<string?> TryReadMarkerDetailsAsync(string cwd, string projectType, CancellationToken ct)
    {
        try
        {
            return projectType switch
            {
                "node" => await ReadPackageJsonAsync(cwd, ct).ConfigureAwait(false),
                "dotnet" => ReadCsprojDetails(cwd),
                "rust" => await ReadTomlSectionAsync(Path.Combine(cwd, "Cargo.toml"), "package", ct).ConfigureAwait(false),
                "go" => await ReadGoModAsync(cwd, ct).ConfigureAwait(false),
                "python" => await ReadTomlSectionAsync(Path.Combine(cwd, "pyproject.toml"), "project", ct).ConfigureAwait(false),
                _ => null
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InitCommand.TryReadMarkerDetailsAsync failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ReadPackageJsonAsync(string cwd, CancellationToken ct)
    {
        var content = await fileSystem.ReadTextFileAsync(Path.Combine(cwd, "package.json"), ct).ConfigureAwait(false);
        if (content is null) return null;

        var sb = new StringBuilder("package.json:\n");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  name: {ExtractJsonField(content, "name")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  description: {ExtractJsonField(content, "description")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  version: {ExtractJsonField(content, "version")}");
        var deps = ExtractJsonField(content, "scripts");
        if (deps is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  scripts: {deps[..Math.Min(deps.Length, 500)]}");
        return sb.ToString();
    }

    private static string? ReadCsprojDetails(string cwd)
    {
        var csproj = Directory.GetFiles(cwd, "*.csproj")
            .Concat(Directory.GetFiles(cwd, "*.slnx"))
            .FirstOrDefault();
        return csproj is null ? null : $".NET project file: {Path.GetFileName(csproj)}";
    }

    private async Task<string?> ReadTomlSectionAsync(string path, string section, CancellationToken ct)
    {
        var content = await fileSystem.ReadTextFileAsync(path, ct).ConfigureAwait(false);
        if (content is null) return null;

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)} [{section}]:");
        var inSection = false;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                inSection = trimmed.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSection && trimmed.Contains('='))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {trimmed}");
        }
        return sb.ToString();
    }

    private async Task<string?> ReadGoModAsync(string cwd, CancellationToken ct)
    {
        var content = await fileSystem.ReadTextFileAsync(Path.Combine(cwd, "go.mod"), ct).ConfigureAwait(false);
        if (content is null) return null;

        var sb = new StringBuilder("go.mod:\n");
        foreach (var line in content.Split('\n').Take(10))
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {line.Trim()}");
        return sb.ToString();
    }

    private static string? ExtractJsonField(string json, string field)
    {
        var key = $"\"{field}\"";
        var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0) return null;
        var start = colonIdx + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
        if (start >= json.Length) return null;

        if (json[start] == '"')
        {
            var end = json.IndexOf('"', start + 1);
            return end > start ? json[(start + 1)..end] : null;
        }
        var commaEnd = json.IndexOfAny([',', '\n', '}'], start);
        return commaEnd > start ? json[start..commaEnd].Trim() : json[start..].Trim();
    }

    // 静态模板回退

    private static string BuildStaticTemplate(string cwd)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AGENTS.md");
        sb.AppendLine();
        sb.AppendLine("This file provides guidance to AI agents working with this codebase.");
        sb.AppendLine("Keep it updated as the project evolves.");
        sb.AppendLine();
        sb.AppendLine("## Project Overview");
        sb.AppendLine();
        sb.AppendLine("<!-- Describe what this project does, its purpose, and key stakeholders -->");
        sb.AppendLine();
        sb.AppendLine("## Build & Test Commands");
        sb.AppendLine();
        sb.AppendLine("```bash");
        if (File.Exists(Path.Combine(cwd, "package.json")))
            sb.AppendLine("npm install\nnpm run build\nnpm test");
        else if (ProjectCommandDetector.HasMarker(cwd, "*.csproj", "*.vbproj", "*.fsproj", "*.slnx"))
            sb.AppendLine("dotnet build\ndotnet test");
        else if (File.Exists(Path.Combine(cwd, "Cargo.toml")))
            sb.AppendLine("cargo build\ncargo test");
        else if (File.Exists(Path.Combine(cwd, "go.mod")))
            sb.AppendLine("go build ./...\ngo test ./...");
        else
            sb.AppendLine("# Add your build and test commands here");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Project Architecture");
        sb.AppendLine();
        sb.AppendLine("<!-- Describe the directory structure, key modules, and how they interact -->");
        sb.AppendLine();
        sb.AppendLine("## Coding Conventions");
        sb.AppendLine();
        sb.AppendLine("<!-- Document style rules, naming conventions, patterns to follow -->");
        sb.AppendLine();
        sb.AppendLine("## Agent Guidelines");
        sb.AppendLine();
        sb.AppendLine("<!-- Instructions specific to AI agents -->");
        sb.AppendLine();
        sb.AppendLine("## Tool Permissions");
        sb.AppendLine();
        sb.AppendLine("<!-- List any tool restrictions or special permissions -->");
        return sb.ToString();
    }
}
