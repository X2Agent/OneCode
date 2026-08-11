namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// 处理 MCP elicitation 请求，桥接 MCP SDK 与宿主环境（控制台/TUI）。
/// </summary>
/// <remarks>
/// 可选依赖（保留可空）：
/// - <see cref="_promptFunc"/>：控制台输入函数；缺失时返回 <c>defaultYes</c> 默认响应
/// - <see cref="_openBrowserFunc"/>：浏览器打开函数；缺失时记录 warning 并跳过
/// </remarks>
public sealed class McpElicitationHandler
{
    private readonly ILogger<McpElicitationHandler> _logger;
    private readonly Func<string, CancellationToken, Task<string?>>? _promptFunc;
    private readonly Func<string, Task>? _openBrowserFunc;

    public McpElicitationHandler(
        ILogger<McpElicitationHandler> logger,
        Func<string, CancellationToken, Task<string?>>? promptFunc = null,
        Func<string, Task>? openBrowserFunc = null)
    {
        _logger = logger;
        _promptFunc = promptFunc;
        _openBrowserFunc = openBrowserFunc;
    }

    public async Task<ElicitationResponse> HandleElicitationAsync(
        McpElicitationPrompt request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(request.Message))
            return new ElicitationResponse(ElicitationAction.Cancel, null);

        _logger.LogInformation("Elicitation request from '{Server}': {Message}", request.ServerName, request.Message);

        if (!string.IsNullOrEmpty(request.Url))
        {
            var accept = await PromptYesNoAsync(
                $"[MCP Elicitation] {request.Message}\nOpen browser? [Y/n]: ",
                defaultYes: true,
                cancellationToken).ConfigureAwait(false);

            if (!accept)
            {
                return new ElicitationResponse(ElicitationAction.Cancel, null);
            }

            await OpenBrowserAsync(request.Url).ConfigureAwait(false);
            return new ElicitationResponse(ElicitationAction.Accept, null);
        }

        var collectedData = await CollectSchemaDataAsync(request, cancellationToken).ConfigureAwait(false);

        var serialized = collectedData.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(collectedData)
            : null;
        return new ElicitationResponse(ElicitationAction.Accept, serialized);
    }

    /// <summary>
    /// Prompt the user for a yes/no answer. If a <c>promptFunc</c> was injected
    /// at construction time, delegate to it (useful for non-console hosts such as
    /// the TUI or test harnesses); otherwise return the default value.
    /// </summary>
    private async Task<bool> PromptYesNoAsync(
        string prompt,
        bool defaultYes,
        CancellationToken cancellationToken)
    {
        if (_promptFunc is null)
            return defaultYes;

        var answer = await _promptFunc(prompt, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(answer))
            return defaultYes;
        return answer!.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walk the request schema and ask the user for a value per property.
    /// Returns an empty dictionary when no schema is supplied.
    /// </summary>
    private async Task<Dictionary<string, object?>> CollectSchemaDataAsync(
        McpElicitationPrompt request,
        CancellationToken cancellationToken)
    {
        var collected = new Dictionary<string, object?>();
        if (string.IsNullOrEmpty(request.Schema)) return collected;

        try
        {
            using var schemaDoc = System.Text.Json.JsonDocument.Parse(request.Schema);
            if (!schemaDoc.RootElement.TryGetProperty("properties", out var properties))
                return collected;

            foreach (var prop in properties.EnumerateObject())
            {
                var desc = prop.Value.TryGetProperty("description", out var descEl)
                    ? descEl.GetString() ?? prop.Name
                    : prop.Name;
                var defaultVal = prop.Value.TryGetProperty("default", out var defEl)
                    ? defEl.GetString()
                    : null;
                var prompt = defaultVal is not null
                    ? $"  {desc} [{defaultVal}]: "
                    : $"  {desc}: ";

                var input = await PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
                collected[prop.Name] = string.IsNullOrEmpty(input) ? defaultVal : input;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse elicitation schema");
        }

        return collected;
    }

    private async Task<string?> PromptAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_promptFunc is null)
            return null;

        return await _promptFunc(prompt, cancellationToken).ConfigureAwait(false);
    }

    public static McpElicitationHandler CreateWithUserPrompt(
        Func<string, CancellationToken, Task<string?>> promptFunc)
    {
        ArgumentNullException.ThrowIfNull(promptFunc);

        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<McpElicitationHandler>.Instance;
        return new McpElicitationHandler(logger, promptFunc: promptFunc);
    }

    private async Task OpenBrowserAsync(string url)
    {
        if (_openBrowserFunc is null)
        {
            _logger.LogWarning("No browser opener configured, skipping URL: {Url}", url);
            return;
        }

        try
        {
            await _openBrowserFunc(url).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open browser for URL: {Url}", url);
        }
    }
}

public sealed record McpElicitationPrompt(
    string ServerName,
    string Message,
    string? Schema = null,
    string? Url = null);

public sealed record ElicitationResponse(
    ElicitationAction Action,
    string? Data = null);

public enum ElicitationAction
{
    Accept,
    Decline,
    Cancel,
}
