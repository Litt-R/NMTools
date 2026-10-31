namespace NMTools.Core.Updates;

/// <summary>
/// 更新源的唯一事实来源。仓库名写死在代码里，不接受服务器响应中的任意链接，
/// 避免被响应内容引导到仓库以外的地址。
/// </summary>
public static class UpdateSource
{
    public const string Owner = "Litt-R";

    public const string Repository = "NMTools";

    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/" + Owner + "/" + Repository + "/releases/latest";

    public const string ReleasesPageUrl =
        "https://github.com/" + Owner + "/" + Repository + "/releases";

    private const string RepositoryPageUrlPrefix = "https://github.com/" + Owner + "/" + Repository + "/";

    /// <summary>
    /// 判断一个链接是否属于本仓库。只接受 https 且主机为 github.com 的地址，
    /// 并且必须落在 <c>/Litt-R/NMTools/</c> 路径下。
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
                new Uri(RepositoryPageUrlPrefix).AbsolutePath,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 某个 tag 对应的 Release 页面。用于响应中缺少合法链接时的兜底，tag 会被转义。
    /// </summary>
    public static string TagPageUrl(string tagName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        return RepositoryPageUrlPrefix + "tag/" + Uri.EscapeDataString(tagName);
    }
}
