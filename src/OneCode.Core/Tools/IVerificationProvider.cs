namespace OneCode.Core.Tools;

/// <summary>
/// 语言验证提供者接口——在文件编辑后验证代码正确性。
/// </summary>
/// <remarks>
/// Harness Engineering: Post-tool 质量门禁（Layer 1）。
/// 由 <c>VerificationMiddleware</c> 在 EditTransaction 之后调用，
/// 验证失败时把错误回注 LLM 上下文，强制下一轮修复。
/// 实现应通过 <see cref="VerificationProfile"/> 配置驱动，支持多语言。
/// </remarks>
public interface IVerificationProvider
{
    /// <summary>
    /// 检查指定工作目录下的项目是否通过验证（编译/类型检查等）。
    /// </summary>
    /// <param name="workingDirectory">工作目录（通常为项目根目录）。</param>
    /// <param name="modifiedFiles">本次事务中修改的文件列表，用于快速定位错误。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>验证结果，包含错误列表和是否成功。</returns>
    Task<VerificationResult> VerifyAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct = default);

    /// <summary>
    /// 仅执行自动化测试，不隐式重复构建。默认实现保持向后兼容，
    /// 但支持独立测试命令的提供者必须覆盖此方法。
    /// </summary>
    async Task<VerificationResult> VerifyTestsAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct = default)
        => await VerifyAsync(workingDirectory, modifiedFiles, ct).ConfigureAwait(false);

    /// <summary>
    /// 仅执行集成测试。默认实现不假定单元测试与集成测试使用同一命令，
    /// 因此返回明确的未配置结果。
    /// </summary>
    Task<VerificationResult> VerifyIntegrationTestsAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct = default)
        => Task.FromResult(new VerificationResult
        {
            Success = false,
            Skipped = true,
            Errors = [new VerificationError("(integration-test)", 0, 0, "error", "Integration test command is not configured.")],
        });

    /// <summary>
    /// 执行构建和自动化测试。保留给需要组合验证的现有调用方；
    /// 独立质量门禁应分别调用 <see cref="VerifyAsync"/> 和 <see cref="VerifyTestsAsync"/>。
    /// </summary>
    async Task<VerificationResult> VerifyBuildAndTestsAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct = default)
    {
        var build = await VerifyAsync(workingDirectory, modifiedFiles, ct).ConfigureAwait(false);
        if (!build.Success || build.Skipped)
            return build;

        var tests = await VerifyTestsAsync(workingDirectory, modifiedFiles, ct).ConfigureAwait(false);
        return new VerificationResult
        {
            Success = build.Success && tests.Success,
            Skipped = build.Skipped && tests.Skipped,
            Errors = [.. build.Errors, .. tests.Errors],
            Duration = build.Duration + tests.Duration,
        };
    }

    /// <summary>
    /// 判断指定文件路径是否被任何已注册的 profile 支持（按扩展名匹配）。
    /// 中间件用此方法决定是否对该文件计数。
    /// </summary>
    bool IsSourceFile(string filePath);
}

/// <summary>
/// 验证结果。
/// </summary>
public sealed record VerificationResult
{
    public required bool Success { get; init; }
    public required IReadOnlyList<VerificationError> Errors { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// 验证被跳过（无匹配 profile 或构建工具未安装）。
    /// Skipped ≠ Success — 不应视为"已验证通过"。
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// 格式化为 LLM 可读的错误摘要（用于回注工具结果）。
    /// </summary>
    public string FormatForLlm()
    {
        if (Skipped) return "Verification skipped (no matching profile or build tool available).";
        if (Success) return "Verification succeeded.";

        var errorCount = Errors.Count;
        var visibleErrors = Errors.Take(10).ToList();
        var lines = new List<string>(visibleErrors.Count + 2);

        lines.Add($"Verification failed with {errorCount} error(s):");
        foreach (var e in visibleErrors)
        {
            lines.Add(e.ToString());
        }

        if (errorCount > 10)
            lines.Add($"... and {errorCount - 10} more error(s).");

        return string.Join("\n", lines);
    }
}

/// <summary>
/// 单条验证错误。
/// </summary>
public sealed record VerificationError(
    string File,
    int Line,
    int Column,
    string Severity,
    string Message)
{
    public override string ToString()
    {
        var severity = Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "ERROR" : Severity.ToUpperInvariant();
        return $"  {severity} {File}({Line},{Column}): {Message}";
    }
}
