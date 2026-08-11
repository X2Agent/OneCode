using System.Text;
using OneCode.Core.Prompt;

namespace OneCode.App.Services;

/// <summary>
/// Composes shared <c>system/harness.prompt</c> with role- or product-specific prompt bodies.
/// Single source of truth for harness text used by main session, forked Explore/Plan agents, and Team workers.
/// </summary>
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
    /// Main-session system prompt: harness + rendered <c>system/default.prompt</c> placeholders.
    /// </summary>
    public async Task<string> ComposeMainAsync(
        string systemContext,
        string userContext,
        string? memorySection,
        string? availableTools,
        CancellationToken ct = default)
    {
        var harness = await GetHarnessAsync(ct).ConfigureAwait(false);
        var template = await promptManager.GetPromptAsync(DefaultPromptName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Base prompt '{DefaultPromptName}' not found in any IPromptManager store.");

        var body = RenderDefaultPrompt(template, systemContext, userContext, memorySection, availableTools);
        return Compose(harness, body);
    }

    /// <summary>
    /// Worker / fork system prompt: harness + role-specific body (Team role file or Explore/Plan overlay).
    /// </summary>
    public async Task<string> ComposeWithRoleAsync(string roleBody, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleBody);
        var harness = await GetHarnessAsync(ct).ConfigureAwait(false);
        return Compose(harness, roleBody);
    }

    internal static string Compose(string harness, string body)
    {
        var sb = new StringBuilder(harness.Length + body.Length + 2);
        sb.Append(harness.TrimEnd());
        sb.Append("\n\n");
        sb.Append(body.Trim());
        return sb.ToString();
    }

    private static string RenderDefaultPrompt(
        string template,
        string systemContext,
        string userContext,
        string? memorySection,
        string? availableTools)
    {
        var sb = new StringBuilder(template);

        sb.Replace("{{system_context}}", systemContext ?? string.Empty);
        sb.Replace("{{available_tools}}", availableTools ?? string.Empty);
        sb.Replace("{{user_context}}",
            string.IsNullOrWhiteSpace(userContext) || userContext == "(No additional user context)"
                ? string.Empty : userContext);
        sb.Replace("{{memory_section}}", memorySection ?? string.Empty);

        return sb.ToString().TrimEnd();
    }
}
