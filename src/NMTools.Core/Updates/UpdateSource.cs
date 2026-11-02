namespace NMTools.Core.Updates;

/// <summary>
/// 更新源的唯一事实来源。仓库名写死在代码里，不接受服务器响应中的任意链接，
/// 避免被响应内容引导到仓库以外的地址。
/// </summary>
public static class UpdateSource
{
    public const string Owner = "Litt-R";

    public const string Repository = "NMTools";

    private const string RepositoryUrlPrefix = "https://github.com/" + Owner + "/" + Repository + "/";

    /// <summary>
    /// 最新稳定版端点。请求时<b>不跟随重定向</b>：GitHub 只在存在「最新、非草稿、非预发布」
    /// 的 release 时返回 302，<c>Location</c> 指向该 tag；否则返回 404。
    /// 这与 API 的 <c>releases/latest</c> 语义一致，也是坚持「不接受预发布」的关键
    /// （见 ADR 0006）。注意它和下方的 API 地址不是一回事：本端点属于网页，
    /// 不消耗 <c>api.github.com</c> 的匿名配额。
    /// </summary>
    public const string LatestReleaseRedirectUrl = RepositoryUrlPrefix + "releases/latest";

    /// <summary>
    /// 全部 release 的 Atom 订阅源，用于取更新说明。它包含预发布和草稿，
    /// 因此只能用来按已知 tag 查找，不能用来决定「最新版本是哪个」。
    /// </summary>
    public const string ReleasesAtomUrl = RepositoryUrlPrefix + "releases.atom";

    /// <summary>
    /// 某个 tag 的资产列表片段（网页展开 Assets 时加载的那段 HTML），
    /// 提供资产名、体积和 SHA-256 摘要。
    /// </summary>
    public static string ExpandedAssetsUrl(string tagName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        return RepositoryUrlPrefix + "releases/expanded_assets/" + Uri.EscapeDataString(tagName);
    }

    /// <summary>
    /// 按发布脚本的命名规则拼接资产下载地址。下载地址不再来自响应正文，
    /// 而是由已校验的 tag 和 <see cref="UpdateManifest.ExpectedArchiveName"/> 推导。
    /// </summary>
    public static string ReleaseDownloadUrl(string tagName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        if (!ReleaseVersion.TryParseStableTag(tagName, out var version))
        {
            throw new ArgumentException("tag 必须是稳定版版本号", nameof(tagName));
        }

        return RepositoryUrlPrefix
            + "releases/download/"
            + Uri.EscapeDataString(tagName)
            + "/"
            + UpdateManifest.ExpectedArchiveName(version);
    }

    /// <summary>
    /// 从 <see cref="LatestReleaseRedirectUrl"/> 的 <c>Location</c> 里取出 tag，
    /// 例如 <c>https://github.com/Litt-R/NMTools/releases/tag/v0.7.0</c> → <c>v0.7.0</c>。
    /// 地址不属于本仓库的 tag 路径时返回 null。
    /// </summary>
    public static string? TryExtractTagFromReleaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsTrustedReleaseUrl(url))
        {
            return null;
        }

        const string marker = "/releases/tag/";
        var path = uri.AbsolutePath;
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var tag = Uri.UnescapeDataString(path[(index + marker.Length)..]).Trim('/');
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    /// <summary>
    /// 判断一个下载链接是否属于本仓库的发布资产。只接受 https 且主机为 github.com 的地址，
    /// 并且必须落在 <c>/Litt-R/NMTools/</c> 路径下。
    ///
    /// 这里的校验对象只有下载地址，不是供用户点击的页面链接——程序不打开任何外部页面。
    /// </summary>
    public static bool IsTrustedReleaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.Contains("..", StringComparison.Ordinal)
            && (uri.AbsolutePath + "/").StartsWith(
                new Uri(RepositoryUrlPrefix).AbsolutePath,
                StringComparison.OrdinalIgnoreCase);
    }
}
