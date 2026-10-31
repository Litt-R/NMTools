namespace NMTools.Core.Updates;

/// <summary>
/// 从 GitHub Releases API 的 <c>releases/latest</c> 响应中解析出的更新清单。
/// 只保留界面需要展示的字段，不保留响应原文。
/// </summary>
/// <param name="Version">release tag 解析出的稳定版版本号。</param>
/// <param name="TagName">原始 tag 文本，仅用于界面显示。</param>
/// <param name="Notes">更新说明（GitHub 的 release body），可能为空。</param>
/// <param name="DownloadUrl">与发布包同名的 zip 资产地址；没有匹配资产时为 null。</param>
/// <param name="DownloadSizeBytes">zip 资产大小；未知时为 null。</param>
/// <param name="ReleasePageUrl">该版本的 GitHub Release 页面，用于「下载并更新」。</param>
/// <param name="ChecksumUrl">校验文件（<c>SHA256SUMS.txt</c>）的资产地址；没有时为 null。</param>
public sealed record UpdateManifest(
    Version Version,
    string TagName,
    string Notes,
    string? DownloadUrl,
    long? DownloadSizeBytes,
    string ReleasePageUrl,
    string? ChecksumUrl = null)
{
    /// <summary>
    /// 下载包期望的 SHA-256。由下载流程在取得校验文件后填入，
    /// 不来自 API 响应本身——清单文件才是校验值的来源。
    /// </summary>
    public string? ExpectedArchiveChecksum { get; init; }

    /// <summary>
    /// 是否具备自动更新的完整条件：有下载地址和校验文件。
    /// 缺少任一条件时只能退回到"打开浏览器手动下载"。
    /// </summary>
    public bool SupportsAutomaticUpdate =>
        DownloadUrl is not null && ChecksumUrl is not null;

    public UpdateManifest WithExpectedChecksum(string? checksum) =>
        this with { ExpectedArchiveChecksum = checksum };

    /// <summary>
    /// 更新说明的最大保留长度。GitHub 的 release body 可能非常长，
    /// 界面只用一个小滚动区域展示，超出部分在解析阶段就截断，避免无谓的内存占用。
    /// </summary>
    public const int MaxNotesLength = 8000;

    /// <summary>
    /// 更新包文件名的形态：<c>NMTools-v0.7.0-win-x64.zip</c>。
    /// 便携包由 <c>scripts/publish-portable.ps1</c> 按同一规则命名。
    /// </summary>
    public static string ExpectedArchiveName(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var normalized = ReleaseVersion.Normalize(version);
        return $"NMTools-v{normalized.Major}.{normalized.Minor}.{normalized.Build}-win-x64.zip";
    }
}
