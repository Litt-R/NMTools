using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class UpdateSourceTests
{
    [Theory]
    [InlineData("https://github.com/Litt-R/NMTools/releases/tag/v0.7.0")]
    [InlineData("https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip")]
    [InlineData("https://github.com/litt-r/nmtools/releases/tag/v0.7.0")]
    [InlineData("https://github.com/Litt-R/NMTools")]
    public void IsTrustedReleaseUrl_AcceptsRepositoryUrls(string url)
    {
        Assert.True(UpdateSource.IsTrustedReleaseUrl(url));
    }

    [Theory]
    [InlineData("http://github.com/Litt-R/NMTools/releases/tag/v0.7.0")] // 非 https
    [InlineData("https://evil.example.com/Litt-R/NMTools/releases/tag/v0.7.0")]
    [InlineData("https://github.com/Other/NMTools/releases/tag/v0.7.0")]
    [InlineData("https://github.com/Litt-R/OtherRepo/releases/tag/v0.7.0")]
    [InlineData("https://github.com/")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsTrustedReleaseUrl_RejectsEverythingElse(string? url)
    {
        Assert.False(UpdateSource.IsTrustedReleaseUrl(url));
    }

    [Fact]
    public void IsTrustedReleaseUrl_RejectsPathTraversal()
    {
        Assert.False(UpdateSource.IsTrustedReleaseUrl(
            "https://github.com/Litt-R/NMTools/../../evil/thing"));
    }

    [Fact]
    public void Endpoints_PointAtGithubWebHostNotApi()
    {
        // ADR 0006：不再访问 api.github.com——匿名配额会被共用出口 IP 打满。
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/releases/latest",
            UpdateSource.LatestReleaseRedirectUrl);
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/releases.atom",
            UpdateSource.ReleasesAtomUrl);
        Assert.DoesNotContain(
            "api.github.com",
            UpdateSource.LatestReleaseRedirectUrl + UpdateSource.ReleasesAtomUrl,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpandedAssetsUrl_UsesTagPath()
    {
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/releases/expanded_assets/v0.7.0",
            UpdateSource.ExpandedAssetsUrl("v0.7.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExpandedAssetsUrl_RejectsEmptyTag(string? tag)
    {
        Assert.ThrowsAny<ArgumentException>(() => UpdateSource.ExpandedAssetsUrl(tag!));
    }

    [Fact]
    public void ReleaseDownloadUrl_MatchesPublishScriptNaming()
    {
        // 下载地址由 tag 推导，不再取自响应正文（ADR 0006）。
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip",
            UpdateSource.ReleaseDownloadUrl("v0.7.0"));
    }

    [Theory]
    [InlineData("v0.7.0-beta.1")]
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData(null)]
    public void ReleaseDownloadUrl_RejectsNonStableTag(string? tag)
    {
        Assert.ThrowsAny<ArgumentException>(() => UpdateSource.ReleaseDownloadUrl(tag!));
    }

    [Theory]
    [InlineData("https://github.com/Litt-R/NMTools/releases/tag/v0.7.0", "v0.7.0")]
    [InlineData("https://github.com/Litt-R/NMTools/releases/tag/v0.7.0/", "v0.7.0")]
    [InlineData("https://github.com/litt-r/nmtools/releases/tag/v0.7.0", "v0.7.0")]
    public void TryExtractTagFromReleaseUrl_ReadsTagFromLink(string url, string expected)
    {
        Assert.Equal(expected, UpdateSource.TryExtractTagFromReleaseUrl(url));
    }

    [Theory]
    [InlineData("https://github.com/Litt-R/NMTools/releases")]           // 没有 tag 段
    [InlineData("https://github.com/Litt-R/NMTools/releases/download/v0.7.0/x.zip")] // 不是 tag 路径
    [InlineData("https://evil.example.com/Litt-R/NMTools/releases/tag/v0.7.0")]
    [InlineData("https://github.com/Other/Repo/releases/tag/v0.7.0")]
    [InlineData("")]
    [InlineData(null)]
    public void TryExtractTagFromReleaseUrl_RejectsForeignUrls(string? url)
    {
        Assert.Null(UpdateSource.TryExtractTagFromReleaseUrl(url));
    }
}
