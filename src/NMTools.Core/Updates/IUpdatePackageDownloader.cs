namespace NMTools.Core.Updates;

/// <summary>
/// 更新包下载与暂存的抽象。Core 定义"下载什么、校验什么、失败怎么收场"，
/// 真实 HTTP 和文件写入由 <c>NMTools.App</c> 实现，便于用假实现覆盖各种中断场景。
/// </summary>
public interface IUpdatePackageDownloader
{
    /// <summary>
    /// 下载指定版本的便携包到暂存位置，并校验大小与 SHA-256。
    ///
    /// 实现必须保证：任一环节失败都清理本次下载的临时文件，不留下半成品；
    /// 不覆盖 <paramref name="stagingDirectory"/> 之外的文件。
    /// </summary>
    /// <param name="manifest">清单，提供下载地址与声明大小。</param>
    /// <param name="stagingDirectory">暂存目录，调用方保证位于发布包内且可写。</param>
    /// <param name="progress">可选进度回调，参数为已下载字节数。</param>
    Task<DownloadResult> DownloadAsync(
        UpdateManifest manifest,
        string stagingDirectory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <param name="ArchivePath">校验通过后的 zip 路径；失败时为 null。</param>
/// <param name="FailureReason">失败原因分类。</param>
/// <param name="ExpectedChecksum">清单声明的 SHA-256；缺失时为 null。</param>
/// <param name="ActualChecksum">实际下载内容的 SHA-256；失败时可能为 null。</param>
public sealed record DownloadResult(
    string? ArchivePath,
    UpdateDownloadFailureReason FailureReason,
    string? ExpectedChecksum = null,
    string? ActualChecksum = null)
{
    public bool IsSuccess => ArchivePath is not null && FailureReason == UpdateDownloadFailureReason.None;

    public static DownloadResult Success(string archivePath, string? checksum = null) =>
        new(archivePath, UpdateDownloadFailureReason.None, checksum, checksum);

    public static DownloadResult Failure(UpdateDownloadFailureReason reason) =>
        new(null, reason);
}

public enum UpdateDownloadFailureReason
{
    None,

    /// <summary>网络不可达、连接中断。</summary>
    Network,

    /// <summary>请求超时。</summary>
    Timeout,

    /// <summary>HTTP 非成功状态码。</summary>
    HttpStatus,

    /// <summary>下载体积与清单声明不符，或超出上限。</summary>
    SizeMismatch,

    /// <summary>SHA-256 与清单声明不符，说明内容被篡改或下载损坏。</summary>
    ChecksumMismatch,

    /// <summary>暂存目录不可写或磁盘空间不足。</summary>
    StorageFailure,

    /// <summary>调用方取消。</summary>
    Canceled,
}

/// <summary>解包失败的原因。所有路径相关校验都在 <see cref="IUpdatePackageExtractor"/> 里完成。</summary>
public enum UpdateExtractFailureReason
{
    None,

    /// <summary>zip 已损坏，或不是合法的 zip 文件。</summary>
    InvalidArchive,

    /// <summary>归档包含路径穿越、绝对路径或超出暂存目录的条目。</summary>
    UnsafeEntry,

    /// <summary>解压后的文件数量或总体积超出上限。</summary>
    LimitExceeded,

    /// <summary>归档中缺少必需的文件（例如 <c>program/NMTools.App.exe</c>）。</summary>
    MissingRequiredFile,

    /// <summary>磁盘写入失败。</summary>
    StorageFailure,

    /// <summary>调用方取消。</summary>
    Canceled,
}

/// <summary>
/// 把校验通过的 zip 解到暂存目录。实现必须拒绝任何逃逸出目标目录的路径。
/// </summary>
public interface IUpdatePackageExtractor
{
    /// <summary>
    /// 解压 <paramref name="archivePath"/> 到 <paramref name="destinationDirectory"/>。
    /// 目标目录会被清空后重建；调用方保证它是 NMTools 自己的暂存目录。
    /// </summary>
    UpdateExtractResult Extract(string archivePath, string destinationDirectory);
}

/// <param name="ExtractedDirectory">解压结果根目录；失败时为 null。</param>
public sealed record UpdateExtractResult(
    string? ExtractedDirectory,
    UpdateExtractFailureReason FailureReason)
{
    public bool IsSuccess => ExtractedDirectory is not null && FailureReason == UpdateExtractFailureReason.None;

    public static UpdateExtractResult Success(string directory) =>
        new(directory, UpdateExtractFailureReason.None);

    public static UpdateExtractResult Failure(UpdateExtractFailureReason reason) =>
        new(null, reason);
}
