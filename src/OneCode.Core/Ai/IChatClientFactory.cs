using Microsoft.Extensions.AI;

namespace OneCode.Core.Ai;

/// <summary>
/// Creates provider-specific <see cref="IChatClient"/> instances and decorator chains.
/// </summary>
public interface IChatClientFactory
{
    IChatClient CreateBaseClient(
        string providerId,
        string apiKey,
        string? baseUrl = null,
        string? model = null,
        ILoggerFactory? loggerFactory = null,
        int? ollamaNumCtx = null);

    IChatClient CreateWithDecorators(
        string providerId,
        string apiKey,
        string? baseUrl = null,
        string? model = null,
        ILoggerFactory? loggerFactory = null,
        int? ollamaNumCtx = null,
        int streamMaxRetries = 6,
        int streamIdleTimeoutSeconds = 180);
}
