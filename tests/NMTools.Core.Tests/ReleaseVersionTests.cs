using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.7.0", 0, 7, 0, 0)]
    [InlineData("0.7.0", 0, 7, 0, 0)]
    [InlineData("V1.2.3", 1, 2, 3, 0)]
    [InlineData("v1.0", 1, 0, 0, 0)]
    [InlineData("v2.3.4.5", 2, 3, 4, 5)]
    [InlineData("  v0.7.1  ", 0, 7, 1, 0)]
    public void TryParseStableTag_AcceptsStableTags(
        string tag,
        int major,
        int minor,
        int build,
        int revision)
    {
        Assert.True(ReleaseVersion.TryParseStableTag(tag, out var version));
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData("v0.7.0-beta.1")]
    [InlineData("0.7.0-rc1")]
    [InlineData("v0.7.0+build5")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("vv0.7.0")]
    [InlineData("v0..7")]
    [InlineData("release-0.7.0")]
    public void TryParseStableTag_RejectsNonStableTags(string? tag)
    {
        Assert.False(ReleaseVersion.TryParseStableTag(tag, out _));
    }

    [Fact]
    public void IsNewer_ComparesNumericallyNotLexically()
    {
        // 字符串比较会把 "0.6.10" 判为比 "0.6.9" 更旧，这是版本检查最容易踩的坑。
        Assert.True(ReleaseVersion.IsNewer(new Version(0, 6, 10), new Version(0, 6, 9)));
        Assert.False(ReleaseVersion.IsNewer(new Version(0, 6, 9), new Version(0, 6, 10)));
    }

    [Theory]
    [InlineData(0, 7, 0, 0, 0, 6, 1, 0, true)]
    [InlineData(1, 0, 0, 0, 0, 6, 1, 0, true)]
    [InlineData(0, 6, 1, 0, 0, 6, 1, 0, false)]
    [InlineData(0, 6, 0, 0, 0, 6, 1, 0, false)]
    [InlineData(0, 6, 1, 1, 0, 6, 1, 0, true)]
    public void IsNewer_HandlesEqualAndOlderVersions(
        int candidateMajor,
        int candidateMinor,
        int candidateBuild,
        int candidateRevision,
        int currentMajor,
        int currentMinor,
        int currentBuild,
        int currentRevision,
        bool expected)
    {
        var candidate = new Version(candidateMajor, candidateMinor, candidateBuild, candidateRevision);
        var current = new Version(currentMajor, currentMinor, currentBuild, currentRevision);

        Assert.Equal(expected, ReleaseVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void IsNewer_TreatsUnsetSegmentsAsZero()
    {
        // Version(0,6,1) 的 Build 后的段为 -1，直接比较会得到错误结论。
        Assert.False(ReleaseVersion.IsNewer(new Version(0, 6, 1), new Version(0, 6, 1, 0)));
        Assert.True(ReleaseVersion.IsNewer(new Version(0, 6, 2), new Version(0, 6, 1)));
    }

    [Fact]
    public void Normalize_FillsMissingSegmentsWithZero()
    {
        Assert.Equal(new Version(0, 6, 1, 0), ReleaseVersion.Normalize(new Version(0, 6, 1)));
        Assert.Equal(new Version(0, 6, 0, 0), ReleaseVersion.Normalize(new Version(0, 6)));
    }
}
