using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NMTools.App.Infrastructure;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 下载便携包并校验大小与 SHA-256。
///
/// 约束（见 ADR 0005）：
/// - 只允许下载到调用方指定的暂存目录，路径由 <see cref="PortableLayout"/> 决定；
/// - 大小上限固定，超限立即中止，不把整个大文件读进内存；
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

            // 声明大小与清单不符时直接拒绝，避免下载到错误资产。
            if (manifest.DownloadSizeBytes is long declared
                && response.Content.Headers.ContentLength is long actualLength
                && actualLength != declared)
            {
                _log?.Warn(
                    "update.download.sizeMismatch",
                    $"declared={declared} actual={actualLength}");
                return DownloadResult.Failure(UpdateDownloadFailureReason.SizeMismatch);
            }

            var written = await WriteToFileAsync(
                response,
                archivePath,
                progress,
                linkedSource.Token).ConfigureAwait(false);

            if (written is null)
            {
                return DownloadResult.Failure(UpdateDownloadFailureReason.SizeMismatch);
            }

            if (manifest.DownloadSizeBytes is long expected && written.Value != expected)
            {
                TryDelete(archivePath);
                _log?.Warn(
                    "update.download.sizeMismatch",
                    $"declared={expected} written={written.Value}");
                return DownloadResult.Failure(UpdateDownloadFailureReason.SizeMismatch);
            }

            // 计算实际内容的 SHA-256，交给调用方与清单比对。
            var actualChecksum = ComputeSha256(archivePath);
            _log?.Info(
                "update.download.completed",
                $"bytes={written.Value} checksum={(actualChecksum is null ? "none" : "ok")}");

            return new DownloadResult(
                archivePath,
                UpdateDownloadFailureReason.None,
                manifest.ExpectedArchiveChecksum,
                actualChecksum);
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
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
