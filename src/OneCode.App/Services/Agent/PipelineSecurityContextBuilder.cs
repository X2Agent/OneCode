using OneCode.Core.Coordinator;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware.Contracts;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Shared construction for <see cref="PipelineSecurityContext"/> used by Main and sub-agent paths.
/// Profile-specific fields are passed explicitly; callers use <c>with</c> only when needed.
/// </summary>
internal static class PipelineSecurityContextBuilder
{
    public static PipelineSecurityContext Create(
        string workingDirectory,
        PermissionMode permissionMode,
        IHookExecutionService? hook,
        IPermissionChecker? permissionChecker,
        ITokenLedger? tokenLedger,
        IReadOnlyDictionary<string, PermissionRuleGroup>? rulesBySource = null,
        IReadOnlyDictionary<string, AdditionalWorkingDirectory>? additionalWorkingDirectories = null,
        IVerificationProvider? verificationProvider = null,
        bool enableVerification = false,
        Action<OrchestrationEvent>? orchestrationEventSink = null,
        Action<FileChange>? fileChangeCallback = null,
        string? modelId = null,
        string? providerId = null,
        IReadOnlyList<FileEditContract>? behaviorContracts = null,
        EditTransaction? editTransaction = null,
        SessionId? conversationId = null,
        long? maxBudgetTokens = null)
        => new(
            WorkingDirectory: workingDirectory,
            PermissionMode: permissionMode,
            RulesBySource: rulesBySource,
            AdditionalWorkingDirectories: additionalWorkingDirectories,
            Hook: hook,
            VerificationProvider: verificationProvider,
            EnableVerification: enableVerification,
            OrchestrationEventSink: orchestrationEventSink,
            FileChangeCallback: fileChangeCallback,
            ModelId: modelId,
            ProviderId: providerId,
            BehaviorContracts: behaviorContracts,
            EditTransaction: editTransaction,
            PermissionChecker: permissionChecker,
            TokenLedger: tokenLedger,
            ConversationId: conversationId,
            MaxBudgetTokens: maxBudgetTokens);
}
