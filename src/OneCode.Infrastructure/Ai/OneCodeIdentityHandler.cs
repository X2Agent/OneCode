using OneCode.Core.Product;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// Injects the OneCode client User-Agent on outbound HTTP requests
/// (OpenRouter / Anthropic analytics). Override via ONECODE_IDENTITY_USER_AGENT;
/// empty string disables injection.
/// </summary>
public sealed class OneCodeIdentityHandler : DelegatingHandler
{
    private const string IdentityUserAgentEnvVar = "ONECODE_IDENTITY_USER_AGENT";

    private readonly string _userAgent;

    public OneCodeIdentityHandler()
    {
        var envUa = Environment.GetEnvironmentVariable(IdentityUserAgentEnvVar);
        _userAgent = envUa ?? ProductInfo.Default.UserAgent;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_userAgent))
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
