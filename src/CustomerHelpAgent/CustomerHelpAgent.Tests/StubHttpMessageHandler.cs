using System.Net;
using System.Text;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 測試用的 HTTP 攔截器。手寫而非引入模擬框架，因為方案內沒有 Moq，
/// 為了這一支測試拉進整套模擬框架不划算。
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        this.responder = responder;
    }

    /// <summary>收到的請求。順序與呼叫順序相同。</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>收到的請求 body（與 <see cref="Requests"/> 同索引）。</summary>
    public List<string> RequestBodies { get; } = [];

    public int CallCount => Requests.Count;

    public static StubHttpMessageHandler Json(HttpStatusCode status, string body)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    public static StubHttpMessageHandler Throws(Exception exception)
        => new(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return responder(request);
    }
}

/// <summary>把單一攔截器包成 <see cref="IHttpClientFactory"/>。</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler handler;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        this.handler = handler;
    }

    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
