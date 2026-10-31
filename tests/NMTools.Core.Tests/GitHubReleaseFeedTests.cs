using System.Net;
using System.Net.Http;
using System.Text;
using NMTools.App.Services;
using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

/// <summary>
/// 验证 HTTP 层把各种失败都收敛成结果而不是异常。用假的 HttpMessageHandler 代替真实网络，
/// 因此这些测试不联网、结果稳定。
/// </summary>
public sealed class GitHubReleaseFeedTests
{
    private static readonly Uri RequestUri = new("https://api.github.com/repos/Litt-R/NMTools/releases/latest");

    [Fact]
    public async Task Success_ReturnsJsonBody()
    {
        using var feed = CreateFeed(_ => JsonResponse(HttpStatusCode.OK, "{\"tag_name\":\"v0.7.0\"}"));

        var response = await feed.GetLatestReleaseJsonAsync();

        Assert.True(response.IsSuccess);
        Assert.Equal("{\"tag_name\":\"v0.7.0\"}", response.Json);
        Assert.Equal(UpdateCheckFailureReason.None, response.FailureReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccessStatus_ReportsHttpStatusWithoutThrowing(HttpStatusCode status)
    {
        using var feed = CreateFeed(_ => new HttpResponseMessage(status));

        var response = await feed.GetLatestReleaseJsonAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.HttpStatus, response.FailureReason);
        Assert.Equal((int)status, response.HttpStatusCode);
        Assert.Null(response.Json);
    }

    [Fact]
    public async Task NetworkFailure_ReportsNetworkWithoutThrowing()
    {
        using var feed = CreateFeed(_ => throw new HttpRequestException("connection reset"));

        var response = await feed.GetLatestReleaseJsonAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.Network, response.FailureReason);
    }

    [Fact]
    public async Task Timeout_IsDistinguishedFromCallerCancellation()
    {
        // 超时由 feed 内部的 CTS 触发，调用方没有取消，必须报 Timeout 而不是 Canceled。
        using var feed = CreateFeed(
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            TimeSpan.FromMilliseconds(80));

        var response = await feed.GetLatestReleaseJsonAsync();

        Assert.Equal(UpdateCheckFailureReason.Timeout, response.FailureReason);
    }

    [Fact]
    public async Task CallerCancellation_ReportsCanceled()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        using var feed = CreateFeed(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });

        var response = await feed.GetLatestReleaseJsonAsync(source.Token);

        Assert.Equal(UpdateCheckFailureReason.Canceled, response.FailureReason);
    }

    [Fact]
    public async Task OversizedDeclaredContent_IsRejected()
    {
        using var feed = CreateFeed(_ =>
        {
            var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            // 声明一个超大的 Content-Length，但实际不发送那么多字节。
            content.Headers.ContentLength = 8L * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await feed.GetLatestReleaseJsonAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.InvalidResponse, response.FailureReason);
    }

    [Fact]
    public async Task EmptyBody_ReachesParserAndIsRejected()
    {
        using var feed = CreateFeed(_ => JsonResponse(HttpStatusCode.OK, string.Empty));

        var response = await feed.GetLatestReleaseJsonAsync();

        // 空正文本身不是 HTTP 层错误（服务器返回了 200），由清单解析器判为无效。
        Assert.True(response.IsSuccess);
        Assert.Null(UpdateManifestParser.Parse(response.Json));
    }

    [Fact]
    public async Task DisposedFeed_ReturnsCanceledInsteadOfThrowing()
    {
        var feed = CreateFeed(_ => JsonResponse(HttpStatusCode.OK, "{}"));
        feed.Dispose();

        var response = await feed.GetLatestReleaseJsonAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.Canceled, response.FailureReason);
    }

    [Fact]
    public async Task Request_CarriesNoUserDataAndUsesAnonymousGet()
    {
        HttpRequestMessage? captured = null;
        using var feed = CreateFeed(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, "{}");
        });

        await feed.GetLatestReleaseJsonAsync();

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal(RequestUri, captured.RequestUri);
        // 不能携带 Authorization/Cookie 之类的凭据。
        Assert.Null(captured.Headers.Authorization);
        Assert.DoesNotContain(captured.Headers, header =>
            header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(captured.Headers.UserAgent, product =>
            product.Product?.Name == "NMTools");
    }

    private static GitHubReleaseFeed CreateFeed(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        TimeSpan? timeout = null)
    {
        return CreateFeed((request, _) => Task.FromResult(handler(request)), timeout);
    }

    private static GitHubReleaseFeed CreateFeed(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        TimeSpan? timeout = null)
    {
        return new GitHubReleaseFeed(
            RequestUri,
            log: null,
            handler: new StubHandler(handler),
            timeout: timeout ?? TimeSpan.FromSeconds(10));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
