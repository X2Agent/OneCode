using OneCode.Core.Domain;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Middleware.Contracts;

/// <summary>
/// 文件编辑行为契约（仅做结构/存在性校验，路径 scope 检查由 PermissionChecker 层的
/// <c>PermissionCheckHelpers.ValidatePath</c> 统一负责）：
/// - Edit Pre: 目标文件必须存在且可写
/// - Post: 编辑后文件仍然存在（不被意外删除）
///
/// 注意：scope 检查下沉到 ValidatePath 是为了正确处理 AdditionalWorkingDirectories
/// （用户通过 /add-dir 添加的额外工作目录），避免本契约因只持 workingDirectory 而误拒。
/// 语言特定的后置验证（如 .cs → dotnet build）应由 Agent 通过 Bash 工具主动执行，
/// 或通过 VerificationMiddleware 的编辑后验证机制触发，而非在契约中硬编码。
///
/// 设计要点：
/// - ExtractPath 委托给 ToolArgumentExtractor.ExtractFilePath，覆盖所有路径 key 约定
///   （filePath/file_path/path/filepath）。
/// - 工具名大小写统一用 OrdinalIgnoreCase 比较，避免 "edit" 小写绕过存在性检查。
/// - Write 不强制要求父目录存在：多数 Write 工具实现会自动 CreateDirectory，
///   强制要求会阻断"Write 到新路径"的核心用例。
/// - catch 显式排除 OperationCanceledException 以保留取消传播。
/// </summary>
public sealed class FileEditContract(string workingDirectory)
{
    public IReadOnlySet<string> ApplicableTools => ToolNames.FileEditTools;

    public ValueTask<ContractResult> ValidatePreConditionsAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        var path = ExtractPath(parameters);
        if (path is null)
            return new(ContractResult.Skipped("No path parameter found."));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(ContractResult.Failed("Invalid file path.", $"Path: {path}"));
        }

        // Edit 特定检查：目标文件必须存在。
        // Write 不要求文件已存在（创建新文件是合法用例），亦不强制父目录存在
        // （多数 Write 工具实现会自动 CreateDirectory）。
        if (IsEdit(toolName) && !File.Exists(fullPath))
            return new(ContractResult.Failed(
                "Target file does not exist.",
                $"Path: {fullPath}"));

        return new(ContractResult.Passed);
    }

    public ValueTask<ContractResult> ValidatePostConditionsAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        object? executionResult,
        CancellationToken ct)
    {
        var path = ExtractPath(parameters);
        if (path is null)
            return new(ContractResult.Skipped("No path parameter."));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(ContractResult.Skipped("Invalid path."));
        }

        // 后置检查：编辑后文件应存在
        if (!File.Exists(fullPath))
            return new(ContractResult.Failed(
                "File does not exist after edit.",
                $"Path: {fullPath}"));

        return new(ContractResult.Passed);
    }

    public string BuildRecoveryGuidance(ContractFailed failure) =>
        $"""
        [BEHAVIOR CONTRACT VIOLATION] {failure.Description}
        {failure.Details}
        Recovery steps:
        1. Re-read the target file's current content
        2. Fix the issue causing the failure
        3. Re-submit the edit
        """;

    private static bool IsEdit(string toolName)
        => string.Equals(toolName, "Edit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 通过 ToolArgumentExtractor 统一提取路径，覆盖所有路径 key 约定
    /// （filePath/file_path/path/filepath）。
    /// </summary>
    private static string? ExtractPath(IReadOnlyDictionary<string, object?> parameters)
        => ToolArgumentExtractor.ExtractFilePath(parameters);
}
