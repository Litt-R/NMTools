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
    /// 查找指定资产名对应的体积和摘要。找不到资产时返回 <see cref="AssetInfo.Empty"/>。
    /// </summary>
    public static AssetInfo Find(string? html, string assetName)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(assetName))
        {
            return AssetInfo.Empty;
        }

        // 按资产行切分：每个资产是 <li> 里的一段，摘要和体积都在同一段。
        // 直接定位资产链接，再截取到下一个资产链接之前的片段。
        var anchor = FindAssetAnchor(html, assetName);
        if (!anchor.Success)
        {
            return AssetInfo.Empty;
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
        return new AssetInfo(ReadDigest(window), ReadDeclaredSizeBytes(window));
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
/// <param name="SizeBytes">资产字节数；未知时为 null。</param>
public sealed record AssetInfo(string? Sha256, long? SizeBytes)
{
    public static AssetInfo Empty { get; } = new(null, null);
}
