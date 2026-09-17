using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Domain;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware.Contracts;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// Single edit-lifecycle middleware (W2-B): FileEdit contract pre-checks, then optional
/// post-edit verification. Replaces separate <c>ContractMiddleware</c> + <c>VerificationMiddleware</c> installs.
/// Weak FileEdit post-conditions (file-still-exists) are dropped; verification covers post-edit quality.
/// </summary>
public static class EditGuardMiddleware
{
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(
            IReadOnlyList<FileEditContract>? contracts,
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

            // --- Pre: behavior contracts (FileEdit exists / path) ---
            if (contracts is { Count: > 0 })
            {
                var parameters = ToolArgumentExtractor.ToParameterDictionary(ctx.Arguments, toolName, logger);
                if (parameters is null)
                {
                    return ToolResult.Error(
                        $"[CONTRACT] Parameter extraction failed for tool '{toolName}' — blocked to prevent fail-open.");
                }

                var stateBagForPre = AIAgent.CurrentRunContext?.Session?.StateBag;
                if (stateBagForPre is null)
                {
                    logger.LogWarning("EditGuardMiddleware: StateBag unavailable, skipping contract pre-checks");
                }
                else
                {
                    foreach (var contract in contracts.Where(c => c.ApplicableTools.Contains(toolName)))
                    {
                        var preResult = await contract.ValidatePreConditionsAsync(toolName, parameters, ct)
                            .ConfigureAwait(false);
                        if (preResult is ContractFailed preFail)
                            return ToolResult.Error(contract.BuildRecoveryGuidance(preFail));
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
}
