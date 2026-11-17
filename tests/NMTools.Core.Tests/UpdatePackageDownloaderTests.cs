using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using NMTools.App.Services;
using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

/// <summary>
/// 验证下载器的校验边界：体积只是显示值、不能决定成败；SHA-256 是唯一的内容校验。
///
/// 这组测试存在的理由：0.7.1 把网页上四舍五入的 "69.9 MB" 换算成字节数，
/// 再要求下载结果与之相等，导致所有自动更新都报"大小不正确"。
/// 用真实的 73,252,268 字节 / 69.9 MB 组合把它钉住。
/// </summary>
public sealed class UpdatePackageDownloaderTests
{
    private const long RealArchiveBytes = 73_252_268;
    private const string ArchiveName = "NMTools-v0.7.2-win-x64.zip";

    [Fact]
    public async Task DeclaredSizeThatDiffersFromArchiveDoesNotFailTheDownload()
    {
        // 页面显示 69.9 MB，换算后 73,295,462 字节，比真实文件多 43,194 字节。
        // 这正是 0.7.2 现场的数值，旧实现会在这里判为失败。
        var bytes = Payload(RealArchiveBytes);
        var manifest = CreateManifest(declaredSize: 73_295_462, sha256: Sha256(bytes));

        var result = await DownloadAsync(manifest, bytes);

        Assert.True(result.IsSuccess, $"体积是近似值，不应导致失败：{result.FailureReason}");
    }

    [Fact]
    public async Task UnreachableAssetList_DegradesInsteadOfBlockingTheUpdate()
    {
        // ADR 0006：资产列表拿不到只是降级，不能因此让用户更新不了。
        var bytes = Payload(1024);
        var manifest = CreateManifest(
            declaredSize: null,
            sha256: null,
            status: AssetLookupStatus.PageMissing);

        var result = await DownloadAsync(manifest, bytes);

        Assert.True(result.IsSuccess, $"网络降级不应阻断更新：{result.FailureReason}");
    }

    [Fact]
    public async Task DigestMissingFromParsedPage_StillInstallsButIsReportedAsParserFailure()
    {
        // 页面拿到了却读不出摘要：放行，但必须让维护者看见解析器已失效。
        var bytes = Payload(1024);
        var manifest = CreateManifest(
            declaredSize: null,
            sha256: null,
            status: AssetLookupStatus.DigestMissing);

        var result = await DownloadAsync(manifest, bytes);

        Assert.True(result.IsSuccess, $"解析器失效不该让用户更新不了：{result.FailureReason}");
        Assert.Equal(AssetLookupStatus.DigestMissing, manifest.AssetStatus);
    }

    [Fact]
    public async Task DigestMismatchFailsEvenWhenDeclaredSizeMatches()
    {
        // 体积完全吻合也不够：内容被替换时必须拒绝。
        var bytes = Payload(1024);
        var wrongDigest = Sha256(Payload(2048));
        var manifest = CreateManifest(declaredSize: 1024, sha256: wrongDigest);

        var result = await DownloadAsync(manifest, bytes);

        Assert.Equal(UpdateDownloadFailureReason.ChecksumMismatch, result.FailureReason);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeWriting()
    {
        var bytes = Payload(1024);
        var manifest = CreateManifest(declaredSize: null, sha256: Sha256(bytes));

        var result = await DownloadAsync(manifest, bytes, declaredContentLength: UpdatePackageDownloader.MaxArchiveBytes + 1);

        Assert.Equal(UpdateDownloadFailureReason.SizeMismatch, result.FailureReason);
    }

    [Fact]
    public async Task SuccessLeavesTheArchiveOnDisk()
    {
        var bytes = Payload(4096);
        var manifest = CreateManifest(declaredSize: 69_295_462, sha256: Sha256(bytes));
        using var root = new TempRoot();

        var result = await DownloadAsync(manifest, bytes, root: root.Path);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(result.ArchivePath));
        Assert.Equal(bytes.Length, new FileInfo(result.ArchivePath!).Length);
    }

    private static async Task<DownloadResult> DownloadAsync(
        UpdateManifest manifest,
        byte[] body,
        long? declaredContentLength = null,
        string? root = null)
    {
        using var owned = root is null ? new TempRoot() : null;
        var staging = Path.Combine(root ?? owned!.Path, "staging");
        using var downloader = new UpdatePackageDownloader(
            log: null,
            handler: new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new LengthConstrainedContent(body, declaredContentLength),
            }),
            timeout: TimeSpan.FromSeconds(10));

        return await downloader.DownloadAsync(manifest, staging);
    }

    private static UpdateManifest CreateManifest(
        long? declaredSize,
        string? sha256,
        AssetLookupStatus status = AssetLookupStatus.Resolved)
    {
        return new UpdateManifest(
            new Version(0, 7, 2),
            "v0.7.2",
            Notes: string.Empty,
            DownloadUrl: $"https://github.com/Litt-R/NMTools/releases/download/v0.7.2/{ArchiveName}",
            DownloadSizeBytes: declaredSize)
        {
            ExpectedSha256 = sha256,
            AssetStatus = status,
        };
    }

    /// <summary>生成可预测的字节内容，避免每条测试都分配真实的几十 MB。</summary>
    private static byte[] Payload(long length)
    {
        var bytes = new byte[length];
        for (long i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NMTools_dl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 返回给定字节内容，并允许单独指定 Content-Length，
    /// 用于模拟"服务器声明的长度与实际不符"或"超出上限"的情况。
    /// </summary>
    private sealed class LengthConstrainedContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly long? _declaredLength;

        public LengthConstrainedContent(byte[] bytes, long? declaredLength)
        {
            _bytes = bytes;
            _declaredLength = declaredLength;
            if (declaredLength is long length)
            {
                Headers.ContentLength = length;
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength ?? _bytes.Length;
            return true;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_respond());
    }
}
