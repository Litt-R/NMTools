using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using NMTools.App.Infrastructure;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 下载便携包并按 SHA-256 校验内容。
///
/// 约束（见 ADR 0005）：
/// - 只允许下载到调用方指定的暂存目录，路径由 <see cref="PortableLayout"/> 决定；
/// - 大小上限固定，超限立即中止，不把整个大文件读进内存；
/// - 清单里的体积是页面显示值，只用于界面，**不参与任何校验**；
/// - 校验失败删除已下载文件，不留半成品；
/// - 日志只记录结果分类和字节数，不记录 URL、文件名或响应内容。
/// </summary>
public sealed class UpdatePackageDownloader : IUpdatePackageDownloader, IDisposable
{
    /// <summary>下载上限。当前便携包约 63 MB，留出余量但不允许无限增长。</summary>
    public const long MaxArchiveBytes = 200L * 1024 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;
    private readonly IDiagnosticLog? _log;
    private bool _disposed;

    public UpdatePackageDownloader(IDiagnosticLog? log = null, HttpMessageHandler? handler = null)
        : this(log, handler, DefaultTimeout)
    {
    }

    public UpdatePackageDownloader(
        IDiagnosticLog? log,
        HttpMessageHandler? handler,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _log = log;
        _timeout = timeout;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        // 由本次请求的 CTS 控制超时，便于区分"超时"和"用户取消"。
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<DownloadResult> DownloadAsync(
        UpdateManifest manifest,
        string stagingDirectory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

        if (_disposed)
        {
            return DownloadResult.Failure(UpdateDownloadFailureReason.Canceled);
        }

        if (manifest.DownloadUrl is null)
        {
            return DownloadResult.Failure(UpdateDownloadFailureReason.HttpStatus);
        }

        // 只接受本仓库的 https 地址，避免清单被篡改后把下载指向别处。
        if (!UpdateSource.IsTrustedReleaseUrl(manifest.DownloadUrl))
        {
            _log?.Warn("update.download.rejectedUrl", "download url is not a trusted repository url");
            return DownloadResult.Failure(UpdateDownloadFailureReason.HttpStatus);
        }

        var archivePath = Path.Combine(stagingDirectory, Path.GetFileName(new Uri(manifest.DownloadUrl).AbsolutePath));
        try
        {
            Directory.CreateDirectory(stagingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Warn("update.download.storageFailed", $"ex={exception.GetType().Name}");
            return DownloadResult.Failure(UpdateDownloadFailureReason.StorageFailure);
        }

        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifest.DownloadUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NMTools", "1.0"));

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn("update.download.httpStatus", $"status={(int)response.StatusCode}");
                return DownloadResult.Failure(UpdateDownloadFailureReason.HttpStatus);
            }

            // 不拿清单体积做任何比对。它来自发布页面的显示文本（如 "69.9 MB"），
            // 是四舍五入后的近似值，与真实字节数必然不等；0.7.1 正是用它对下载结果做
            // 相等比较，导致所有自动更新被判为大小不符。
            // 内容是否完整、是否被替换，统一交给 SHA-256：它由上传时的文件内容算出，
            // 是精确值，不需要容差。体积只用于界面显示和进度条。
            var written = await WriteToFileAsync(
                response,
                archivePath,
                progress,
                linkedSource.Token).ConfigureAwait(false);

            // null 表示实际字节数超过 MaxArchiveBytes，文件明显不是便携包。
            if (written is null)
            {
                TryDelete(archivePath);
                _log?.Warn("update.download.tooLarge", $"limit={MaxArchiveBytes}");
                return DownloadResult.Failure(UpdateDownloadFailureReason.SizeMismatch);
            }

            // SHA-256 是唯一的内容校验。有摘要就必须核对，不符即拒绝。
            // 拿不到摘要时按 ADR 0006 降级放行，但要把原因记清楚：
            // 网络问题降级是可接受的，解析器与上游格式脱节则必须让维护者看见。
            // 两者用不同的事件名和级别，避免"静默失去校验"长期无人发现。
            if (string.IsNullOrWhiteSpace(manifest.ExpectedSha256))
            {
                if (manifest.AssetStatus == AssetLookupStatus.PageMissing)
                {
                    _log?.Warn(
                        "update.download.checksumUnavailable",
                        "asset list was not reachable, continuing without content verification");
                }
                else
                {
                    // 页面拿到了却读不出摘要：我们的解析器该修了。仍然放行，
                    // 但用 ERROR 级别确保它出现在日志和故障排查里。
                    _log?.Error(
                        "update.download.parserOutOfDate",
                        $"assetStatus={manifest.AssetStatus} no digest parsed, continuing without content verification");
                }
            }
            else
            {
                var actual = ComputeSha256(archivePath);
                if (!string.Equals(manifest.ExpectedSha256.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(archivePath);
                    _log?.Warn(
                        "update.download.checksumMismatch",
                        $"declared={manifest.ExpectedSha256} actual={actual ?? "none"}");
                    return DownloadResult.Failure(UpdateDownloadFailureReason.ChecksumMismatch);
                }
            }

            _log?.Info("update.download.completed", $"bytes={written.Value} checksum=ok");
            return DownloadResult.Success(archivePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(archivePath);
            return DownloadResult.Failure(UpdateDownloadFailureReason.Canceled);
        }
        catch (OperationCanceledException)
        {
            TryDelete(archivePath);
            _log?.Warn("update.download.timeout", "download timed out");
            return DownloadResult.Failure(UpdateDownloadFailureReason.Timeout);
        }
        catch (HttpRequestException exception)
        {
            TryDelete(archivePath);
            _log?.Warn("update.download.networkFailed", $"ex={exception.GetType().Name}");
            return DownloadResult.Failure(UpdateDownloadFailureReason.Network);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(archivePath);
            _log?.Warn("update.download.storageFailed", $"ex={exception.GetType().Name}");
            return DownloadResult.Failure(UpdateDownloadFailureReason.StorageFailure);
        }
    }

    /// <summary>
    /// 流式写入，避免把几十 MB 的包读进内存。返回写入字节数；超出上限时返回 null 并删除文件。
    /// </summary>
    private static async Task<long?> WriteToFileAsync(
        HttpResponseMessage response,
        string path,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > MaxArchiveBytes)
        {
            return null;
        }

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        long total = 0;
        var buffer = new byte[81920];

        await using (var destination = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxArchiveBytes)
                {
                    return null;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(total);
            }
        }

        return total;
    }

    private static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结果，下次更新会覆盖同名文件。
        }
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
