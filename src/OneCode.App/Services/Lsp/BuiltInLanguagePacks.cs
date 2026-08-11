using OneCode.Core.Lsp;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// Built-in language pack definitions for common languages.
/// Each pack maps file extensions to an LSP server and includes install instructions.
/// Users can add more packs via ~/.onecode/lsp/*.json (see <see cref="LanguagePackRegistry"/>).
/// </summary>
public static class BuiltInLanguagePacks
{
    /// <summary>All built-in language packs.</summary>
    public static IReadOnlyList<LanguagePack> All { get; } =
    [
        CreateCSharpPack(),
        CreateTypeScriptPack(),
        CreatePythonPack(),
        CreateGoPack(),
        CreateRustPack(),
    ];

    private static LanguagePack CreateCSharpPack() => new()
    {
        Id = "csharp",
        DisplayName = "C#",
        Extensions = [".cs", ".csx"],
        ProjectFiles = ["*.csproj", "*.sln", "*.slnx"],
        Server = new LanguageServerSpec { Command = "csharp-ls" },
        Install = new LanguagePackInstall
        {
            Windows = "dotnet tool install --global csharp-ls",
            Unix = "dotnet tool install --global csharp-ls",
            DetectionCommand = "csharp-ls --version",
            Prerequisites = ["dotnet"],
        },
    };

    private static LanguagePack CreateTypeScriptPack() => new()
    {
        Id = "typescript",
        DisplayName = "TypeScript / JavaScript",
        Extensions = [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts"],
        ProjectFiles = ["package.json", "tsconfig.json"],
        Server = new LanguageServerSpec
        {
            Command = "typescript-language-server",
            Args = ["--stdio"],
        },
        Install = new LanguagePackInstall
        {
            Windows = "npm install -g typescript-language-server typescript",
            Unix = "npm install -g typescript-language-server typescript",
            DetectionCommand = "typescript-language-server --version",
            Prerequisites = ["node", "npm"],
        },
    };

    private static LanguagePack CreatePythonPack() => new()
    {
        Id = "python",
        DisplayName = "Python",
        Extensions = [".py", ".pyi"],
        ProjectFiles = ["pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile"],
        Server = new LanguageServerSpec
        {
            Command = "pyright-langserver",
            Args = ["--stdio"],
        },
        Install = new LanguagePackInstall
        {
            Windows = "pip install pyright",
            Unix = "pip install pyright",
            DetectionCommand = "pyright-langserver --version",
            Prerequisites = ["python"],
        },
    };

    private static LanguagePack CreateGoPack() => new()
    {
        Id = "go",
        DisplayName = "Go",
        Extensions = [".go"],
        ProjectFiles = ["go.mod", "go.work"],
        Server = new LanguageServerSpec { Command = "gopls" },
        Install = new LanguagePackInstall
        {
            Windows = "go install golang.org/x/tools/gopls@latest",
            Unix = "go install golang.org/x/tools/gopls@latest",
            DetectionCommand = "gopls version",
            Prerequisites = ["go"],
        },
    };

    private static LanguagePack CreateRustPack() => new()
    {
        Id = "rust",
        DisplayName = "Rust",
        Extensions = [".rs"],
        ProjectFiles = ["Cargo.toml"],
        Server = new LanguageServerSpec { Command = "rust-analyzer" },
        Install = new LanguagePackInstall
        {
            Windows = "rustup component add rust-analyzer",
            Unix = "rustup component add rust-analyzer",
            DetectionCommand = "rust-analyzer --version",
            Prerequisites = ["rustup"],
        },
    };
}
