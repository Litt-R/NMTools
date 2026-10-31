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

    private const string RepositoryUrlPrefix = "https://github.com/" + Owner + "/" + Repository + "/";

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
