using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using NMTools.App.Infrastructure;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 从 GitHub Releases API 拉取最新发布。
///
/// 约束（见 ADR 0004）：
/// - 只发一次匿名 GET，不带任何用户数据、不带凭据、不带输入正文或主机信息；
/// - 所有网络/HTTP/超时异常都收敛为 <see cref="ReleaseFeedResponse.Failure"/>，绝不抛出；
/// - 响应正文只在内存中交给解析器，日志只记录状态码和耗时，不记录正文。
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 响应体上限。正常响应约几十 KB；设置上限是为了防止异常响应把内存吃光。
    /// 超出上限视为响应无效，不读取剩余内容。
    /// </summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly IDiagnosticLog? _log;
    private readonly Uri _requestUri;
    private bool _disposed;

    public GitHubReleaseFeed(IDiagnosticLog? log = null, HttpMessageHandler? handler = null)
        : this(new Uri(UpdateSource.LatestReleaseApiUrl), log, handler, DefaultTimeout)
    {
    }

    /// <summary>
    /// 供测试注入请求地址、处理器和超时。生产代码使用只带日志的公共构造函数。
    /// </summary>
    public GitHubReleaseFeed(
        Uri requestUri,
        IDiagnosticLog? log,
        HttpMessageHandler? handler,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _requestUri = requestUri;
        _timeout = timeout;
        _log = log;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);

        // 超时统一由本次请求的 CTS 控制：HttpClient 自身的超时会抛 TaskCanceledException，
        // 与调用方取消混在一起，无法区分"超时"和"用户取消"。
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<ReleaseFeedResponse> GetLatestReleaseJsonAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return ReleaseFeedResponse.Failure(UpdateCheckFailureReason.Canceled);
        }

        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _requestUri);
            // GitHub 要求带 User-Agent；Accept 指定 v3 JSON 以拿到稳定结构。
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NMTools", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 403/429 通常是未认证调用的速率限制，与网络错误一样按"稍后重试"处理。
                _log?.Warn(
                    "update.check.httpStatus",
                    $"status={(int)response.StatusCode} reason={response.StatusCode}");
                return ReleaseFeedResponse.Failure(
                    UpdateCheckFailureReason.HttpStatus,
                    (int)response.StatusCode);
            }

            var json = await ReadBodyAsync(response, linkedSource.Token).ConfigureAwait(false);
            return json is null
                ? ReleaseFeedResponse.Failure(UpdateCheckFailureReason.InvalidResponse, (int)response.StatusCode)
                : ReleaseFeedResponse.Success(json);
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
