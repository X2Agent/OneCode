using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Compact;

public enum ToolProtocolErrorCode
{
    DuplicateCallId,
    DuplicateResult,
    MissingResult,
    OrphanResult,
    BatchInterrupted,
    InvalidRole,
    InvalidOrdering,
}

public sealed record ToolProtocolError(
    ToolProtocolErrorCode Code,
    string CallId,
    int MessageIndex,
    string Message);

public sealed record ToolProtocolValidationResult(IReadOnlyList<ToolProtocolError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ToolProtocolValidationResult Valid { get; } = new([]);
}

public interface IToolProtocolValidator
{
    ToolProtocolValidationResult Validate(IReadOnlyList<ChatMessage> messages);
}

/// <summary>
/// Validates the provider-facing function-call protocol. A call batch must be followed by
/// result messages until every call is closed; unrelated messages cannot interrupt it.
/// </summary>
public sealed class ToolProtocolValidator : IToolProtocolValidator
{
    public ToolProtocolValidationResult Validate(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        List<ToolProtocolError> errors = [];
        var seenCalls = new HashSet<string>(StringComparer.Ordinal);
        var seenResults = new HashSet<string>(StringComparer.Ordinal);
        var pendingCalls = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            var results = message.Contents.OfType<FunctionResultContent>().ToList();

            if (calls.Count > 0 && message.Role != ChatRole.Assistant)
            {
                foreach (var call in calls)
                    errors.Add(Error(ToolProtocolErrorCode.InvalidRole, call.CallId, index,
                        "Function calls must use the assistant role."));
            }

            if (results.Count > 0 && message.Role != ChatRole.User)
            {
                foreach (var result in results)
                    errors.Add(Error(ToolProtocolErrorCode.InvalidRole, result.CallId, index,
                        "Function results must use the user role."));
            }

            if (pendingCalls.Count > 0 && results.Count == 0)
            {
                errors.Add(Error(
                    ToolProtocolErrorCode.BatchInterrupted,
                    pendingCalls.First(),
                    index,
                    "A tool-call batch was interrupted before all results were received."));
            }

            foreach (var call in calls)
            {
                if (string.IsNullOrWhiteSpace(call.CallId))
                {
                    errors.Add(Error(ToolProtocolErrorCode.InvalidOrdering, "", index,
                        "Function call is missing a call ID."));
                    continue;
                }

                if (!seenCalls.Add(call.CallId))
                {
                    errors.Add(Error(ToolProtocolErrorCode.DuplicateCallId, call.CallId, index,
                        "Function call ID is duplicated."));
                    continue;
                }

                pendingCalls.Add(call.CallId);
            }

            foreach (var result in results)
            {
                if (string.IsNullOrWhiteSpace(result.CallId))
                {
                    errors.Add(Error(ToolProtocolErrorCode.InvalidOrdering, "", index,
                        "Function result is missing a call ID."));
                    continue;
                }

                if (!seenResults.Add(result.CallId))
                {
                    errors.Add(Error(ToolProtocolErrorCode.DuplicateResult, result.CallId, index,
                        "Function result is duplicated."));
                    continue;
                }

                if (!seenCalls.Contains(result.CallId))
                {
                    errors.Add(Error(ToolProtocolErrorCode.OrphanResult, result.CallId, index,
                        "Function result has no preceding call."));
                    continue;
                }

                if (!pendingCalls.Remove(result.CallId))
                {
                    errors.Add(Error(ToolProtocolErrorCode.InvalidOrdering, result.CallId, index,
                        "Function result does not belong to the active batch."));
                }
            }
        }

        foreach (var callId in pendingCalls)
        {
            errors.Add(Error(
                ToolProtocolErrorCode.MissingResult,
                callId,
                messages.Count,
                "Function call has no corresponding result."));
        }

        return errors.Count == 0
            ? ToolProtocolValidationResult.Valid
            : new ToolProtocolValidationResult(errors);
    }

    private static ToolProtocolError Error(
        ToolProtocolErrorCode code,
        string? callId,
        int index,
        string message)
        => new(code, callId ?? "", index, message);
}

public sealed class ToolProtocolException(ToolProtocolValidationResult validation)
    : InvalidOperationException(BuildMessage(validation))
{
    public ToolProtocolValidationResult Validation { get; } = validation;

    private static string BuildMessage(ToolProtocolValidationResult validation)
        => "Invalid tool protocol: " + string.Join("; ", validation.Errors.Select(error =>
            $"{error.Code}[{error.CallId}]@{error.MessageIndex}: {error.Message}"));
}
