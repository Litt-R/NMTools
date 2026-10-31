using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class UpdateDisplayTests
{
    [Theory]
    [InlineData(0, 7, 0, 0, "v0.7.0")]
    [InlineData(0, 6, 1, 0, "v0.6.1")]
    [InlineData(2, 3, 4, 5, "v2.3.4.5")]
    public void FormatVersion_UsesThreeSegmentsUnlessRevisionIsNonZero(
        int major,
        int minor,
        int build,
        int revision,
        string expected)
    {
        Assert.Equal(expected, UpdateDisplay.FormatVersion(new Version(major, minor, build, revision)));
    }

    [Fact]
    public void FormatHeadline_ShowsBothVersions()
    {
        var headline = UpdateDisplay.FormatHeadline(new Version(0, 7, 0, 0), new Version(0, 6, 1, 0));

        Assert.Equal("新版本 v0.7.0（当前 v0.6.1）", headline);
    }

    [Theory]
    [InlineData("有内容", "有内容")]
    [InlineData("  有内容  ", "有内容")]
    [InlineData("", UpdateDisplay.NoNotesPlaceholder)]
    [InlineData("   ", UpdateDisplay.NoNotesPlaceholder)]
    [InlineData(null, UpdateDisplay.NoNotesPlaceholder)]
    public void FormatNotes_FallsBackToPlaceholder(string? notes, string expected)
    {
        Assert.Equal(expected, UpdateDisplay.FormatNotes(notes));
    }

    [Theory]
    [InlineData(512L, "1 KB")]
    [InlineData(2048L, "2 KB")]
    [InlineData(12_845_123L, "12.3 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    public void FormatSize_FormatsReadableValue(long? bytes, string? expected)
    {
        Assert.Equal(expected, UpdateDisplay.FormatSize(bytes));
    }
}
