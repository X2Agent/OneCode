namespace OneCode.Core.Lsp;

/// <summary>
/// Defines a language pack — a declarative spec for how to start and install
/// an LSP server for a specific language. Users can add new languages by
/// dropping a JSON file into ~/.onecode/lsp/ without writing any code.
/// </summary>
public sealed record LanguagePack
{
    /// <summary>Unique identifier, e.g. "csharp", "python".</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name, e.g. "C#".</summary>
    public required string DisplayName { get; init; }

    /// <summary>File extensions this pack handles, e.g. [".cs", ".csx"].</summary>
    public required string[] Extensions { get; init; }

    /// <summary>
    /// Project marker files (glob patterns, top-level only) that indicate the working
    /// directory is a project of this language, e.g. ["go.mod"], ["package.json"],
    /// ["*.csproj", "*.sln"]. When set, the LSP server is only auto-started if at least
    /// one marker is present in the session working directory.
    /// </summary>
    public string[]? ProjectFiles { get; init; }

    /// <summary>How to start the LSP server process.</summary>
    public required LanguageServerSpec Server { get; init; }

    /// <summary>How to install the server binary (optional — may be pre-installed).</summary>
    public LanguagePackInstall? Install { get; init; }

    /// <summary>Convert this pack's server spec to an LspServerConfig.</summary>
    public LspServerConfig ToServerConfig() => new(
        Name: Id,
        Command: Server.Command,
        Args: Server.Args,
        Environment: Server.Env,
        WorkingDirectory: Server.WorkingDirectory,
        InitializationOptions: Server.InitializationOptions);
}

/// <summary>
/// Specifies how to launch an LSP server process.
/// </summary>
public sealed record LanguageServerSpec
{
    /// <summary>Executable command, e.g. "csharp-ls" or "npx".</summary>
    public required string Command { get; init; }

    /// <summary>Command-line arguments.</summary>
    public string[] Args { get; init; } = [];

    /// <summary>Environment variables to set for the server process.</summary>
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>Working directory for the server process (defaults to agent CWD).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>LSP initializationOptions sent in the initialize request.</summary>
    public JsonElement? InitializationOptions { get; init; }
}

/// <summary>
/// Specifies how to install the LSP server binary on each platform.
/// </summary>
public sealed record LanguagePackInstall
{
    /// <summary>Shell command to install on Windows. Null if not installable via script.</summary>
    public string? Windows { get; init; }

    /// <summary>Shell command to install on Unix (Linux/macOS). Null if not installable via script.</summary>
    public string? Unix { get; init; }

    /// <summary>Command to check if the server is already installed (e.g. "csharp-ls --version").</summary>
    public string? DetectionCommand { get; init; }

    /// <summary>Runtime prerequisites, e.g. ["dotnet", "node"]. Checked before install.</summary>
    public string[] Prerequisites { get; init; } = [];
}
