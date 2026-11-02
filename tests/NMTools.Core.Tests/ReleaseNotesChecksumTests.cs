using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class ReleaseNotesChecksumTests
{
    private const string Hash = "89b1e7728396c300d631b4d85241a441a9adf33e3657cba7009c2727d6e73b2d";

    [Theory]
    [InlineData($"SHA256: {Hash}")]
    [InlineData($"SHA-256: {Hash}")]
    [InlineData($"sha256：{Hash}")]
    [InlineData($"SHA-256：`{Hash}`")]
    [InlineData($"**SHA256:** {Hash}")]
    [InlineData($"## What's new\n\n- stuff\n\nSHA256: {Hash}\n")]
    public void Extract_FindsChecksumInCommonFormats(string notes)
    {
        Assert.Equal(Hash, ReleaseNotesChecksum.Extract(notes));
    }

    [Fact]
    public void Extract_NormalizesToLowercase()
    {
        Assert.Equal(Hash, ReleaseNotesChecksum.Extract($"SHA256: {Hash.ToUpperInvariant()}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("no checksum here")]
    [InlineData("SHA256: notahash")]
    [InlineData("SHA256: 89b1e7")]
    [InlineData("SHA256:")]
    public void Extract_ReturnsNullWhenAbsent(string? notes)
    {
        Assert.Null(ReleaseNotesChecksum.Extract(notes));
    }

    [Theory]
    [InlineData(Hash, Hash, true)]
    [InlineData(Hash, "0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData(Hash, null, false)]
    [InlineData(null, Hash, false)]
    [InlineData("", "", false)]
    public void Matches_ComparesCaseInsensitivelyAndRejectsEmpty(
        string? expected,
        string? actual,
        bool shouldMatch)
    {
        Assert.Equal(shouldMatch, ReleaseNotesChecksum.Matches(expected, actual));
    }

    [Fact]
    public void Matches_IgnoresCase()
    {
        Assert.True(ReleaseNotesChecksum.Matches(Hash, Hash.ToUpperInvariant()));
        Assert.True(ReleaseNotesChecksum.Matches(Hash.ToUpperInvariant(), Hash));
    }
}
