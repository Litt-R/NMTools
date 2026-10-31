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
    public void LatestReleaseApiUrl_PointsAtConfiguredRepository()
    {
        Assert.Equal(
            "https://api.github.com/repos/Litt-R/NMTools/releases/latest",
            UpdateSource.LatestReleaseApiUrl);
    }

    [Fact]
    public void TagPageUrl_EscapesTag()
    {
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/tag/v0.7.0%20beta",
            UpdateSource.TagPageUrl("v0.7.0 beta"));
    }
}
