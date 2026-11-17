using System.Globalization;
using System.Text.RegularExpressions;

namespace NMTools.Core.Updates;

/// <summary>
/// 解析 <c>releases/expanded_assets/{tag}</c> 返回的 HTML 片段，取出便携包的
/// 体积和 SHA-256 摘要（见 ADR 0006）。
///
/// 这段 HTML 是给网页渲染用的，不是稳定契约，因此解析只依赖两类不容易变的特征：
/// <c>/releases/download/</c> 资产路径，以及 <c>sha256:</c> 摘要前缀。
/// **不依赖任何 class 名或 DOM 结构**——GitHub 改版时，这里失败应当表现为
/// "没有摘要和体积"（仍然可以更新），而不是崩溃或误判成别的资产。
/// </summary>
public static partial class ExpandedAssetsParser
{
    /// <summary>
    /// 查找指定资产名对应的体积和摘要。
    ///
    /// 返回的 <see cref="AssetInfo.Status"/> 区分了"页面没拿到"和"页面拿到了但读不出摘要"：
    /// 前者是网络问题，按 ADR 0006 降级即可；后者说明本解析器已经和上游格式对不上，
    /// 必须让维护者看见，否则会长期静默地不做任何内容校验。
    /// </summary>
    public static AssetInfo Find(string? html, string assetName)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(assetName))
        {
            return AssetInfo.PageMissing;
        }

        // 按资产行切分：每个资产是 <li> 里的一段，摘要和体积都在同一段。
        // 直接定位资产链接，再截取到下一个资产链接之前的片段。
        var anchor = FindAssetAnchor(html, assetName);
        if (!anchor.Success)
        {
            return AssetInfo.AssetNotFound;
        }

        var window = html[anchor.Index..];
        var nextAsset = DownloadPathPattern().Match(window, 1);
        if (nextAsset.Success)
        {
            window = window[..nextAsset.Index];
        }

        // 体积只用于"合理性检查"（上限、粗略对比），不能当作精确字节数：
        // expanded_assets 里的 "69.9 MB" 是页面上给人和机器都有用的显示值，
        // GitHub 按 1024 进制保留一位小数后四舍五入，与真实字节数的差异可达数万字节。
        // 用它做相等比较会让任何正常下载都判为大小不符。
        var digest = ReadDigest(window);
        return new AssetInfo(
            digest,
            ReadDeclaredSizeBytes(window),
            digest is null ? AssetLookupStatus.DigestMissing : AssetLookupStatus.Resolved);
    }

    /// <summary>
    /// 找到 <c>href="/{owner}/{repo}/releases/download/{tag}/{assetName}"</c> 的位置。
    /// 只匹配路径形式，不匹配绝对地址，避免把页面里别的链接当成资产。
    /// </summary>
    private static Match FindAssetAnchor(string html, string assetName)
    {
        var pattern = $@"releases/download/[^""'/]+/{Regex.Escape(assetName)}""";
        return Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 读取 <c>sha256:&lt;64 位十六进制&gt;</c>。只接受 sha256，其他算法当作没有。
    /// </summary>
    private static string? ReadDigest(string window)
    {
        var match = DigestPattern().Match(window);
        return match.Success ? match.Groups["hash"].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 读取网页上显示的体积文本，例如 <c>68.8 MB</c>，换算成字节。
    ///
    /// **这是近似值，不能用于相等比较。** 页面按 1024 进制保留最多一位小数，
    /// 真实大小与换算结果可能相差数万字节；任何精确性要求都必须由 SHA-256 承担。
    /// 解析不出来时返回 null。
    /// </summary>
    private static long? ReadDeclaredSizeBytes(string window)
    {
        var match = SizePattern().Match(window);
        if (!match.Success)
        {
            return null;
        }

        if (!double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0)
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            _ => 1d,
        };

        return (long)Math.Round(value * multiplier);
    }

    [GeneratedRegex(
        @"sha256:(?<hash>[0-9a-fA-F]{64})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex(
        @">\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>Bytes|KB|MB|GB)\s*<",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    [GeneratedRegex(
        @"releases/download/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadPathPattern();
}

/// <param name="Sha256">小写十六进制摘要；未知时为 null。</param>
/// <param name="SizeBytes">页面上显示的近似字节数；未知时为 null。只用于展示和进度条，不参与校验。</param>
/// <param name="Status">这次查找停在哪一步，用于区分网络降级与解析器失效。</param>
public sealed record AssetInfo(string? Sha256, long? SizeBytes, AssetLookupStatus Status)
{
    /// <summary>资产页面根本没拿到：网络失败、超时或非 2xx。属于可降级的正常情况。</summary>
    public static AssetInfo PageMissing { get; } = new(null, null, AssetLookupStatus.PageMissing);

    /// <summary>页面拿到了，但里面找不到我们的便携包资产链接。多半是上游结构变了。</summary>
    public static AssetInfo AssetNotFound { get; } = new(null, null, AssetLookupStatus.AssetNotFound);

    /// <summary>找到了资产，但没读到 sha256 摘要。解析器大概率已与上游格式脱节。</summary>
    public bool IndicatesParserFailure =>
        Status is AssetLookupStatus.AssetNotFound or AssetLookupStatus.DigestMissing;
}

/// <summary>
/// 资产查找的结果状态。必须把"没拿到页面"和"拿到了但读不出摘要"分开：
/// 前者是网络问题，后者意味着解析器需要维护，两者在日志里不能长得一样。
/// </summary>
public enum AssetLookupStatus
{
    /// <summary>没拿到资产页面（网络、超时、HTTP 非 2xx）。</summary>
    PageMissing,

    /// <summary>拿到了页面，但找不到指定资产的链接。</summary>
    AssetNotFound,

    /// <summary>找到了资产，但没有可用的 sha256 摘要。</summary>
    DigestMissing,

    /// <summary>摘要和体积都读到了。</summary>
    Resolved,
}
