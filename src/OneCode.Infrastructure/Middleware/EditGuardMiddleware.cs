using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// Single edit-lifecycle middleware: Edit-target pre-checks, then optional post-edit verification.
/// Weak file-still-exists post-conditions are dropped; verification covers post-edit quality.
/// Path scope checks belong to <c>PermissionChecker.ValidatePath</c>, which also handles
/// AdditionalWorkingDirectories correctly.
/// </summary>
public static class EditGuardMiddleware
{
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(
            bool enforceEditTargetExists,
            IVerificationProvider? verificationProvider,
            string workingDirectory,
            VerificationOptions? verificationOptions,
            ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var options = verificationOptions ?? VerificationOptions.Default;

        return async (_, ctx, next, ct) =>
        {
            if (ctx.Function is null)
                return await next(ctx, ct).ConfigureAwait(false);

            var toolName = ctx.Function.Name;

            // --- Pre: Edit requires an existing target file ---
            if (enforceEditTargetExists && ToolNames.IsFileEditTool(toolName))
            {
                var parameters = ToolArgumentExtractor.ToParameterDictionary(ctx.Arguments, toolName, logger);
                if (parameters is null)
                {
                    return ToolResult.Error(
                        $"[CONTRACT] Parameter extraction failed for tool '{toolName}' — blocked to prevent fail-open.");
                }

                // Edit 特定检查：目标文件必须存在。Write 不要求（创建新文件是合法用例，
                // 多数 Write 实现会自动 CreateDirectory，不强制父目录存在）。
                if (string.Equals(toolName, "Edit", StringComparison.OrdinalIgnoreCase))
                {
                    var editPath = ToolArgumentExtractor.ExtractFilePath(parameters);
                    if (editPath is not null)
                    {
                        string fullPath;
                        try
                        {
                            fullPath = Path.GetFullPath(editPath, workingDirectory);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            return ToolResult.Error(BuildRecoveryGuidance("Invalid file path.", $"Path: {editPath}"));
                        }

                        if (!File.Exists(fullPath))
                            return ToolResult.Error(BuildRecoveryGuidance("Target file does not exist.", $"Path: {fullPath}"));
                    }
                }
            }

            var result = await next(ctx, ct).ConfigureAwait(false);

            // --- Post: verification (source edits threshold) ---
            if (verificationProvider is null)
                return result;

            var isFileEdit = ToolNames.IsFileEditTool(toolName);
            if (!isFileEdit)
                return result;

            var editedPath = ToolArgumentExtractor.ExtractFilePath(ctx.Arguments);
            if (editedPath is null || !verificationProvider.IsSourceFile(editedPath))
                return result;

            var stateBag = AIAgent.CurrentRunContext?.Session?.StateBag;
            if (stateBag is null)
            {
                logger.LogWarning("EditGuardMiddleware: StateBag unavailable, skipping verification");
                return result;
            }

            stateBag.GetOrInitializeModifiedFiles().Add(editedPath);
            stateBag.IncrementEditsSinceLastBuild();

            if (stateBag.GetEditsSinceLastBuild() < options.Threshold)
                return result;

            var checkResult = await verificationProvider.VerifyAsync(
                workingDirectory,
                stateBag.GetOrInitializeModifiedFiles().ToList(),
                ct).ConfigureAwait(false);

            stateBag.ResetEditsSinceLastBuild();

            if (checkResult.Skipped)
            {
                logger.LogDebug("Verification skipped (no matching profile or build tool)");
                return result;
            }

            if (checkResult.Success)
            {
                logger.LogDebug("Verification passed after {Count} edits", options.Threshold);
                return result;
            }

            stateBag.GetOrInitializeToolExecutionContext().IsVerificationFailure = true;
            logger.LogWarning("Verification failed with {ErrorCount} errors", checkResult.Errors.Count);

            var errorSummary = checkResult.FormatForLlm();
            var existingResult = result as string ?? result?.ToString() ?? "";
            var combinedContent = $"{existingResult}\n\n[VERIFICATION ERROR]\n{errorSummary}";
            return ToolResult.Error(combinedContent, "Fix the verification errors above before making further edits.");
        };
    }

    private static string BuildRecoveryGuidance(string description, string details) =>
        $"""
        [BEHAVIOR CONTRACT VIOLATION] {description}
        {details}
        Recovery steps:
        1. Re-read the target file's current content
        2. Fix the issue causing the failure
        3. Re-submit the edit
        """;
}
