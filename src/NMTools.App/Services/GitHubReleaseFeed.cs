using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using NMTools.App.Infrastructure;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 从 github.com 的网页端点拉取最新发布（见 ADR 0006）。
///
/// 为什么不用 <c>api.github.com</c>：匿名调用是每 IP 每小时 60 次，而公司内所有终端
/// 共用同一个隧道出口，额度会被同事一起打满，表现为周期性的 403。网页端点不消耗该配额。
///
/// 约束：
/// - 三步串行：<c>releases/latest</c>（不跟随重定向，取 302 的 tag）→ <c>releases.atom</c>
///   （更新说明）→ <c>releases/expanded_assets/{tag}</c>（资产名、体积、摘要）；
/// - 只发匿名 GET，不带任何用户数据、不带凭据、不带输入正文或主机信息；
/// - 所有网络/HTTP/超时异常都收敛为 <see cref="ReleaseFeedResponse.Failure"/>，绝不抛出；
/// - 文档只在内存中交给解析器，日志只记录步骤名、状态码和耗时，不记录正文。
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 单份响应体上限。正常文档都是几十 KB 量级；设置上限是为了防止异常响应把内存吃光。
    /// 超出上限视为响应无效，不读取剩余内容。
    /// </summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly IDiagnosticLog? _log;
    private readonly Uri _latestRedirectUri;
    private readonly Uri _atomUri;
    private readonly Func<string, Uri> _expandedAssetsUriFactory;
    private bool _disposed;

    public GitHubReleaseFeed(IDiagnosticLog? log = null, HttpMessageHandler? handler = null)
        : this(
            new Uri(UpdateSource.LatestReleaseRedirectUrl),
            new Uri(UpdateSource.ReleasesAtomUrl),
            tag => new Uri(UpdateSource.ExpandedAssetsUrl(tag)),
            log,
            handler,
            DefaultTimeout)
    {
    }

    /// <summary>
    /// 供测试注入端点、处理器和超时。生产代码使用只带日志的公共构造函数。
    /// </summary>
    public GitHubReleaseFeed(
        Uri latestRedirectUri,
        Uri atomUri,
        Func<string, Uri> expandedAssetsUriFactory,
        IDiagnosticLog? log,
        HttpMessageHandler? handler,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(latestRedirectUri);
        ArgumentNullException.ThrowIfNull(atomUri);
        ArgumentNullException.ThrowIfNull(expandedAssetsUriFactory);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _latestRedirectUri = latestRedirectUri;
        _atomUri = atomUri;
        _expandedAssetsUriFactory = expandedAssetsUriFactory;
        _timeout = timeout;
        _log = log;

        // 生产路径必须自己看 302 的 Location，不能让 HttpClient 替我们跟过去——
        // 跟随之后只能拿到 200 的 HTML 页面，tag 就丢了。注入的测试 handler
        // 自己就是终点，不会跟随，所以不受影响。
        _httpClient = handler is null
            ? new HttpClient(CreateDefaultHandler())
            : new HttpClient(handler, disposeHandler: true);

        // 超时统一由本次请求的 CTS 控制：HttpClient 自身的超时会抛 TaskCanceledException，
        // 与调用方取消混在一起，无法区分"超时"和"用户取消"。
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<ReleaseFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return ReleaseFeedResponse.Failure(UpdateCheckFailureReason.Canceled);
        }

        // 三步共用一个总超时：避免每一步各自 10 秒，最坏情况拖到 30 秒。
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            // 1. 最新稳定版 tag。不跟随重定向——302 的 Location 才是答案，
            //    404 表示上游没有可用的稳定版（含"最新的是预发布"这种情况）。
            var tagName = await ReadLatestTagAsync(linkedSource.Token).ConfigureAwait(false);
            if (tagName is null)
            {
                return ReleaseFeedResponse.Failure(UpdateCheckFailureReason.InvalidResponse);
            }

            // 2. 更新说明。拿不到只是没有说明，不影响更新本身。
            var atomXml = await ReadTextOrNullAsync(_atomUri, linkedSource.Token).ConfigureAwait(false);

            // 3. 资产名、体积和摘要。同样允许缺失，只是失去大小和哈希校验。
            var expandedAssetsHtml = await ReadTextOrNullAsync(
                _expandedAssetsUriFactory(tagName),
                linkedSource.Token).ConfigureAwait(false);

            return ReleaseFeedResponse.Success(
                new ReleaseDocuments(tagName, atomXml, expandedAssetsHtml));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ReleaseFeedResponse.Failure(UpdateCheckFailureReason.Canceled);
        }
        catch (OperationCanceledException)
        {
            // 只触发了超时，调用方没有取消。
            _log?.Warn("update.check.timeout", "request timed out");
            return ReleaseFeedResponse.Failure(UpdateCheckFailureReason.Timeout);
        }
        catch (HttpRequestException exception)
        {
            // 只记录异常类型和 HResult，不记录 URL 查询串或响应内容。
            _log?.Warn("update.check.networkFailed", $"ex={exception.GetType().Name} hr=0x{exception.HResult:X8}");
            return ReleaseFeedResponse.Failure(UpdateCheckFailureReason.Network);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log?.Warn("update.check.unexpected", $"ex={exception.GetType().Name}");
            return ReleaseFeedResponse.Failure(UpdateCheckFailureReason.Network);
        }
    }

    /// <summary>
    /// 请求 <c>releases/latest</c> 并<b>不跟随重定向</b>，从 <c>Location</c> 取出 tag。
    /// GitHub 只在存在最新稳定版时返回 302；404 或其他状态都表示没有可用版本。
    /// </summary>
    private async Task<string?> ReadLatestTagAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(_latestRedirectUri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            var location = response.Headers.Location;
            var tag = UpdateSource.TryExtractTagFromReleaseUrl(
                location?.IsAbsoluteUri == true
                    ? location.ToString()
                    : location is null
                        ? null
                        : new Uri(_latestRedirectUri, location).ToString());

            if (tag is null || !ReleaseVersion.TryParseStableTag(tag, out _))
            {
                // 重定向目标不是本仓库的稳定版 tag：按响应无效处理，不回退到"随便挑一个"。
                _log?.Warn("update.check.unexpectedRedirect", "latest release redirect is not a stable tag");
                return null;
            }

            return tag;
        }

        _log?.Warn(
            "update.check.httpStatus",
            $"step=latest status={(int)response.StatusCode} reason={response.StatusCode}");
        return null;
    }

    /// <summary>
    /// 读取一份可选的文档。非 2xx、超限或读取失败都返回 null——调用方据此降级，
    /// 而不是让整次检查失败。
    /// </summary>
    private async Task<string?> ReadTextOrNullAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log?.Warn(
                "update.check.httpStatus",
                $"step=optional status={(int)response.StatusCode} reason={response.StatusCode}");
            return null;
        }

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 生产用的消息处理器。<c>AllowAutoRedirect=false</c> 是这个类能工作的前提：
    /// 最新版 tag 只存在于 <c>releases/latest</c> 的 302 <c>Location</c> 里。
    /// 独立成方法是为了让测试能直接断言这个策略，而不必反射私有字段。
    /// </summary>
    public static HttpClientHandler CreateDefaultHandler() =>
        new() { AllowAutoRedirect = false };

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // GitHub 要求带 User-Agent。不添加 Accept 指定 API 媒体类型——这里取的是网页内容。
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NMTools", "1.0"));
        return request;
    }

    private static async Task<string?> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}
