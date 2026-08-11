namespace OneCode.Infrastructure.Model;

using Microsoft.Extensions.Logging;
using OneCode.Infrastructure.Config;

/// <summary>
/// 从 https://models.dev/api.json 拉取模型元数据。
/// </summary>
public sealed class ModelsDevClient
{
    private const string ApiUrl = "https://models.dev/api.json";
    private const string HttpClientName = Constants.HttpClientNames.ModelsDev;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelsDevClient> _logger;

    public ModelsDevClient(IHttpClientFactory httpClientFactory, ILogger<ModelsDevClient>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelsDevClient>.Instance;
    }

    /// <summary>
    /// 拉取 models.dev API，返回响应流。失败返回 null。
    /// 调用方负责释放返回的流。
    /// </summary>
    public async Task<Stream?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(ApiUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch models.dev API from {Url}", ApiUrl);
            return null;
        }
    }
}
