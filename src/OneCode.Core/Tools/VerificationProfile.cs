namespace OneCode.Core.Tools;

/// <summary>
/// 验证 profile——描述一种语言的验证命令和错误解析规则。
/// </summary>
/// <remarks>
/// 配置驱动设计：每种语言一条 profile，由 GenericVerificationProvider 按 profile 路由。
/// 对标 opencode formatter 的 {command, extensions} 模式，新增项目标记双重判断和错误正则。
/// </remarks>
public sealed record VerificationProfile
{
    /// <summary>提供者唯一标识，如 "dotnet"、"typescript"、"go"、"rust"。</summary>
    public required string Name { get; init; }

    /// <summary>此 profile 支持的文件扩展名（小写含点），如 [".cs", ".vb", ".fs"]。</summary>
    public required IReadOnlySet<string> FileExtensions { get; init; }

    /// <summary>项目标记文件名列表（用于判断工作目录是否为此语言项目）。支持 glob，如 "*.csproj"。</summary>
    public required IReadOnlySet<string> ProjectMarkers { get; init; }

    /// <summary>验证命令，如 "dotnet"、"npx"、"go"、"cargo"。</summary>
    public required string Command { get; init; }

    /// <summary>命令参数，如 ["build", "--no-restore", "-clp:ErrorsOnly"]。</summary>
    public required IReadOnlyList<string> Args { get; init; }

    /// <summary>
    /// 错误解析正则（多行模式）。必须包含命名组：file, line, col, severity, message。code 可选。
    /// </summary>
    public required string ErrorPattern { get; init; }

    /// <summary>命令执行超时（毫秒）。默认 30 秒。</summary>
    public int TimeoutMs { get; init; } = 30_000;

    /// <summary>探测命令是否可用时检查的命令名（默认同 <see cref="Command"/>）。</summary>
    public string? AvailabilityCommand { get; init; }

    /// <summary>测试命令（供 ProjectCommandDetector 复用），如 "dotnet"。</summary>
    public string? TestCommand { get; init; }

    /// <summary>测试命令参数，如 ["test", "--no-build"]。</summary>
    public IReadOnlyList<string>? TestArgs { get; init; }

    /// <summary>内置默认 profile 列表：dotnet / typescript / go / rust。</summary>
    public static readonly IReadOnlyList<VerificationProfile> BuiltIn =
    [
        new VerificationProfile
        {
            Name = "dotnet",
            FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".vb", ".fs", ".fsx", ".csx" },
            ProjectMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*.csproj", "*.vbproj", "*.fsproj", "*.slnx", "*.sln", "global.json" },
            Command = "dotnet",
            Args = ["build", "--no-restore", "-clp:ErrorsOnly", "--nologo"],
            ErrorPattern = @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s*(?<code>[A-Za-z]+\d+):\s*(?<message>.+)$",
            TestCommand = "dotnet",
            TestArgs = ["test", "--no-build"],
        },
        new VerificationProfile
        {
            Name = "typescript",
            FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".mts", ".cts" },
            ProjectMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tsconfig.json" },
            Command = "npx",
            Args = ["tsc", "--noEmit"],
            ErrorPattern = @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s*(?<code>TS\d+):\s*(?<message>.+)$",
            AvailabilityCommand = "npx",
            TestCommand = "npx",
            TestArgs = ["tsc", "--noEmit"],
        },
        new VerificationProfile
        {
            Name = "go",
            FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".go" },
            ProjectMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "go.mod" },
            Command = "go",
            Args = ["build", "./..."],
            ErrorPattern = @"^(?<file>.+?):(?<line>\d+):(?<col>\d+):\s*(?<message>.+)$",
            TestCommand = "go",
            TestArgs = ["test", "./..."],
        },
        new VerificationProfile
        {
            Name = "rust",
            FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".rs" },
            ProjectMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cargo.toml" },
            Command = "cargo",
            Args = ["check", "--message-format=short"],
            // cargo check --message-format=short 输出格式：
            //   error[E0425]: cannot find value `foo` in this scope --> src/main.rs:2:5
            ErrorPattern = @"^(?<severity>error|warning)\[(?<code>E\d+)\]:\s*(?<message>.+?)\s*-->\s*(?<file>.+?):(?<line>\d+):(?<col>\d+)$",
            TestCommand = "cargo",
            TestArgs = ["test"],
        },
    ];
}
