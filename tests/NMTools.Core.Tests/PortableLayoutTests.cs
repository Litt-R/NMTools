using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class PortableLayoutTests
{
    [Theory]
    [InlineData("data")]
    [InlineData("data/input-bridge-settings.json")]
    [InlineData("data/window-settings.json.bak")]
    [InlineData("logs")]
    [InlineData("logs/NMTools-20261031.log")]
    [InlineData("DATA/anything.json")]
    [InlineData("./data/x.json")]
    public void IsPreservedPath_KeepsUserData(string path)
    {
        Assert.True(PortableLayout.IsPreservedPath(path), $"'{path}' 应被保留。");
    }

    [Theory]
    [InlineData("NMTools.App.exe")]
    [InlineData(".runtime")]
    [InlineData(".runtime/NMTools.App/x.dll")]
    [InlineData("datax/file.json")]
    [InlineData("datas/file.json")]
    [InlineData("logs2/file.log")]
    [InlineData("database/file")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsPreservedPath_DoesNotMatchLookalikes(string? path)
    {
        // 前缀匹配极易写成 StartsWith("data")，那样 "database/" 也会被当成用户数据。
        Assert.False(PortableLayout.IsPreservedPath(path!), $"'{path}' 不应被保留。");
    }

    [Theory]
    [InlineData("NMTools.App.exe")]
    [InlineData("NMTools.Updater.exe")]
    [InlineData("subdir/file.dll")]
    [InlineData(".runtime/x.dll")]
    public void IsReplaceablePath_AcceptsNormalEntries(string path)
    {
        Assert.True(PortableLayout.IsReplaceablePath(path), $"'{path}' 应可替换。");
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("/absolute/path")]
    [InlineData("C:/windows/system32/evil.dll")]
    [InlineData("C:\\windows\\evil.dll")]
    [InlineData(".update-staging/leftover.zip")]
    [InlineData("program.old/file.dll")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsReplaceablePath_RejectsEscapeAndReservedDirectories(string path)
    {
        Assert.False(PortableLayout.IsReplaceablePath(path), $"'{path}' 不应被替换。");
    }

    [Theory]
    [InlineData("program/NMTools.App.exe", "program\\NMTools.App.exe")]
    [InlineData("program/sub/file.dll", "program\\sub\\file.dll")]
    [InlineData("Start-NMTools.vbs", "Start-NMTools.vbs")]
    [InlineData("./program/file.txt", "program\\file.txt")]
    public void ResolveSafeEntryPath_NormalizesValidEntries(string entry, string expected)
    {
        Assert.Equal(expected, PortableLayout.ResolveSafeEntryPath(entry));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("program/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("program/CON")]
    [InlineData("program/NUL.txt")]
    [InlineData("program/COM1.dll")]
    [InlineData("program/LPT9.log")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveSafeEntryPath_RejectsUnsafeEntries(string? entry)
    {
        Assert.Null(PortableLayout.ResolveSafeEntryPath(entry!));
    }

    [Fact]
    public void ResolveSafeEntryPath_RejectsInvalidFileNameCharacters()
    {
        Assert.Null(PortableLayout.ResolveSafeEntryPath("program/bad<char>.txt"));
    }

    [Fact]
    public void Paths_AreLaidOutAsDocumented()
    {
        var app = Path.Combine("root", "NMTools-win-x64", "program");

        Assert.Equal(Path.Combine(app, ".update-staging"), PortableLayout.ResolveStagingDirectory(app));
        Assert.Equal(
            Path.Combine("root", "NMTools-win-x64", "program.old"),
            PortableLayout.ResolveBackupDirectory(app));
        Assert.Equal(
            Path.Combine("root", "NMTools-win-x64", ".update-pending"),
            PortableLayout.ResolvePendingUpdateMarker(app));
    }
}
