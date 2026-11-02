using System.Net;
using System.Net.Http;
using System.Text;
using NMTools.App.Services;
using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

/// <summary>
/// 验证 HTTP 层把各种失败都收敛成结果而不是异常，并验证三步串联的取数逻辑。
/// 用假的 HttpMessageHandler 代替真实网络，因此这些测试不联网、结果稳定。
/// </summary>
public sealed class GitHubReleaseFeedTests
{
    private static readonly Uri LatestUri = new("https://github.com/Litt-R/NMTools/releases/latest");
    private static readonly Uri AtomUri = new("https://github.com/Litt-R/NMTools/releases.atom");

    private const string TagLocation = "https://github.com/Litt-R/NMTools/releases/tag/v0.7.0";

    private const string AtomXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <entry>
        <link rel="alternate" href="https://github.com/Litt-R/NMTools/releases/tag/v0.7.0"/>
        <content type="html">&lt;li&gt;notes&lt;/li&gt;</content>
      </entry>
    </feed>
    """;

    [Fact]
    public async Task Success_ReturnsTagAndDocuments()
    {
        using var feed = CreateFeed(request => Respond(request, RequestKind.Redirect));

        var response = await feed.GetLatestReleaseAsync();

        Assert.True(response.IsSuccess);
        Assert.Equal("v0.7.0", response.Documents!.TagName);
        Assert.Contains("notes", response.Documents.AtomXml);
        Assert.NotNull(response.Documents.ExpandedAssetsHtml);
        Assert.Equal(UpdateCheckFailureReason.None, response.FailureReason);
    }

    [Fact]
    public void ProductionClient_DoesNotFollowRedirects()
    {
        // 不跟随重定向是拿 tag 的前提：跟随之后只能拿到 200 的 HTML 页面，tag 就丢了。
        using var handler = GitHubReleaseFeed.CreateDefaultHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task MissingLatestRelease_ReportsInvalidResponse()
    {
        // 上游没有稳定版（404）——例如最新 release 是预发布——必须报失败而不是回退到别的 tag。
        using var feed = CreateFeed(request => request.RequestUri == LatestUri
            ? StatusResponse(HttpStatusCode.NotFound)
            : Respond(request, RequestKind.Success));

        var response = await feed.GetLatestReleaseAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.InvalidResponse, response.FailureReason);
        Assert.Null(response.Documents);
    }

    [Theory]
    [InlineData("https://github.com/Litt-R/NMTools/releases/tag/v0.7.0-beta.1")]
    [InlineData("https://github.com/Litt-R/NMTools/releases/tag/nightly")]
    [InlineData("https://github.com/Other/Repo/releases/tag/v0.7.0")]
    [InlineData("https://evil.example.com/Litt-R/NMTools/releases/tag/v0.7.0")]
    [InlineData("https://github.com/Litt-R/NMTools/releases")]
    public async Task UntrustedOrUnstableRedirectTarget_IsRejected(string location)
    {
        using var feed = CreateFeed(request => request.RequestUri == LatestUri
            ? Redirect(location)
            : Respond(request, RequestKind.Success));

        var response = await feed.GetLatestReleaseAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.InvalidResponse, response.FailureReason);
    }

    [Fact]
    public async Task OptionalDocumentsFailure_DegradesInsteadOfFailing()
    {
        // atom 和资产列表拿不到时仍应给出 tag，让下载照常进行（只失去说明/校验）。
        using var feed = CreateFeed(request => request.RequestUri == LatestUri
            ? Respond(request, RequestKind.Redirect)
            : StatusResponse(HttpStatusCode.InternalServerError));

        var response = await feed.GetLatestReleaseAsync();

        Assert.True(response.IsSuccess);
        Assert.Equal("v0.7.0", response.Documents!.TagName);
        Assert.Null(response.Documents.AtomXml);
        Assert.Null(response.Documents.ExpandedAssetsHtml);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ApiStyleRateLimit_NoLongerOccursButWouldStillBeContained(HttpStatusCode status)
    {
        // 现在不访问 API，正常不会再遇到 403；万一上游对网页端点也限流，行为必须仍是收敛而非抛出。
        using var feed = CreateFeed(request => request.RequestUri == LatestUri
            ? StatusResponse(status)
            : Respond(request, RequestKind.Success));

        var response = await feed.GetLatestReleaseAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.InvalidResponse, response.FailureReason);
        Assert.Equal((int)status, response.HttpStatusCode ?? (int)status);
    }

    [Fact]
    public async Task NetworkFailure_ReportsNetworkWithoutThrowing()
    {
        using var feed = CreateFeed(_ => throw new HttpRequestException("connection reset"));

        var response = await feed.GetLatestReleaseAsync();

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

        var response = await feed.GetLatestReleaseAsync();

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

        var response = await feed.GetLatestReleaseAsync(source.Token);

        Assert.Equal(UpdateCheckFailureReason.Canceled, response.FailureReason);
    }

    [Fact]
    public async Task OversizedDeclaredContent_IsTreatedAsMissingDocument()
    {
        using var feed = CreateFeed(request =>
        {
            if (request.RequestUri == LatestUri)
            {
                return Respond(request, RequestKind.Redirect);
            }

            var content = new StringContent(string.Empty, Encoding.UTF8, "text/html");
            // 声明一个超大的 Content-Length，但实际不发送那么多字节。
            content.Headers.ContentLength = 8L * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await feed.GetLatestReleaseAsync();

        // 可选文档超限时降级为 null，不影响 tag 本身。
        Assert.True(response.IsSuccess);
        Assert.Null(response.Documents!.AtomXml);
        Assert.Null(response.Documents.ExpandedAssetsHtml);
    }

    [Fact]
    public async Task DisposedFeed_ReturnsCanceledInsteadOfThrowing()
    {
        var feed = CreateFeed(request => Respond(request, RequestKind.Success));
        feed.Dispose();

        var response = await feed.GetLatestReleaseAsync();

        Assert.False(response.IsSuccess);
        Assert.Equal(UpdateCheckFailureReason.Canceled, response.FailureReason);
    }

    [Fact]
    public async Task Request_CarriesNoUserDataAndUsesAnonymousGet()
    {
        HttpRequestMessage? captured = null;
        using var feed = CreateFeed(request =>
        {
            if (request.RequestUri == LatestUri)
            {
                captured = request;
            }

            return Respond(request, request.RequestUri == LatestUri
                ? RequestKind.Redirect
                : RequestKind.Success);
        });

        await feed.GetLatestReleaseAsync();

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal(LatestUri, captured.RequestUri);
        // 不能携带 Authorization/Cookie 之类的凭据。
        Assert.Null(captured.Headers.Authorization);
        Assert.DoesNotContain(captured.Headers, header =>
            header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(captured.Headers.UserAgent, product =>
            product.Product?.Name == "NMTools");
    }

    [Fact]
    public async Task Requests_GoToGithubWebHostOnly_NotApi()
    {
        var hosts = new List<string>();
        using var feed = CreateFeed(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Respond(request, request.RequestUri == LatestUri
                ? RequestKind.Redirect
                : RequestKind.Success);
        });

        await feed.GetLatestReleaseAsync();

        Assert.NotEmpty(hosts);
        Assert.All(hosts, host => Assert.Equal("github.com", host));
        Assert.DoesNotContain(hosts, host => host.Contains("api.", StringComparison.OrdinalIgnoreCase));
    }

    private enum RequestKind
    {
        Success,
        Redirect,
    }

    /// <summary>
    /// 模拟上游：latest 视 kind 返回 302 或 404，其余端点返回 200 的示例文档。
    /// </summary>
    private static HttpResponseMessage Respond(HttpRequestMessage request, RequestKind kind)
    {
        if (request.RequestUri == LatestUri)
        {
            return kind == RequestKind.Redirect
                ? Redirect(TagLocation)
                : StatusResponse(HttpStatusCode.NotFound);
        }

        return request.RequestUri!.AbsolutePath.EndsWith("/releases.atom", StringComparison.Ordinal)
            ? TextResponse(HttpStatusCode.OK, AtomXml, "application/atom+xml")
            : TextResponse(
                HttpStatusCode.OK,
                "<a href=\"/Litt-R/NMTools/releases/download/v0.7.0/x.zip\">x</a>",
                "text/html");
    }

    private static HttpResponseMessage StatusResponse(HttpStatusCode status) =>
        new(status);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode status, string body, string mediaType)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
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
            LatestUri,
            AtomUri,
            tag => new Uri($"https://github.com/Litt-R/NMTools/releases/expanded_assets/{tag}"),
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
}
