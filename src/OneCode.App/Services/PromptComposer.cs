using System.Text;
using OneCode.Core.Prompt;

namespace OneCode.App.Services;

/// <summary>
/// Loads and renders the two prompt halves the agent needs, without joining them.
/// </summary>
/// <remarks>
/// <para>
/// The shared harness fragment and the agent-specific body stay <b>separate</b> all the way to the
/// agent. MAF composes them (harness first, then the agent body, separated by a blank line) via
/// <c>HarnessInstructions</c> and <c>ChatOptions.Instructions</c>, so the product no longer needs a
/// <c>Compose</c> step — and the two inputs can no longer be passed in the wrong order or duplicated
/// by a caller that pre-joined them.
/// </para>
/// <para>
/// What this class owns is loading and rendering: which file wins, and substituting the template
/// placeholders. Composition is not its job.
/// </para>
/// </remarks>
public sealed class PromptComposer(IPromptManager promptManager)
{
    public const string HarnessPromptName = "system/harness";
    public const string DefaultPromptName = "system/default";

    /// <summary>Loads the shared harness fragment. Throws if missing (built-in copy is required).</summary>
    public async Task<string> GetHarnessAsync(CancellationToken ct = default)
    {
        return await promptManager.GetPromptAsync(HarnessPromptName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Harness prompt '{HarnessPromptName}' not found in any IPromptManager store.");
    }

    /// <summary>
    /// Renders the Main agent body: <c>system/default.prompt</c> with its runtime placeholders
    /// substituted. Returned without the harness fragment.
    /// </summary>
    public async Task<string> RenderMainBodyAsync(
        string systemContext,
        string userContext,
        string? availableTools,
        CancellationToken ct = default)
    {
        var template = await promptManager.GetPromptAsync(DefaultPromptName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Base prompt '{DefaultPromptName}' not found in any IPromptManager store.");

        return RenderDefaultPrompt(template, systemContext, userContext, availableTools);
    }

    /// <summary>
    /// Renders a Worker / fork body (role overlay or harness body). Returned without the harness fragment.
    /// </summary>
    public string RenderRoleBody(string roleBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleBody);
        return roleBody.TrimEnd();
    }

    private static string RenderDefaultPrompt(
        string template,
        string systemContext,
        string userContext,
        string? availableTools)
    {
        var sb = new StringBuilder(template);

        sb.Replace("{{system_context}}", systemContext ?? string.Empty);
        sb.Replace("{{available_tools}}", availableTools ?? string.Empty);
        sb.Replace("{{user_context}}",
            string.IsNullOrWhiteSpace(userContext) || userContext == "(No additional user context)"
                ? string.Empty : userContext);

        return sb.ToString().TrimEnd();
    }
}
