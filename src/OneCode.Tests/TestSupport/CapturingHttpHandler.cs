using System.Net;
using System.Text;

namespace OneCode.Tests.TestSupport;

/// <summary>
/// 终止型 <see cref="DelegatingHandler"/>：记录出站请求体并直接返回预置响应，不发起真实网络调用。
/// 用于断言 provider 适配器最终序列化出的 wire 级 JSON——这是唯一能证明
/// "我们写入的 provider 参数真的会被发出去" 的层级。
/// </summary>
internal sealed class CapturingHttpHandler(string responseBody) : DelegatingHandler
{
    private readonly List<string> _requestBodies = [];
    private readonly List<Uri> _requestUris = [];

    public IReadOnlyList<string> RequestBodies => _requestBodies;

    public IReadOnlyList<Uri> RequestUris => _requestUris;

    public string LastRequestBody => _requestBodies[^1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requestUris.Add(request.RequestUri!);
        if (request.Content is not null)
            _requestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }
}
