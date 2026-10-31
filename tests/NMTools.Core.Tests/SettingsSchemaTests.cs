using NMTools.Core;

namespace NMTools.Core.Tests;

public sealed class SettingsSchemaTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(8, false)]
    [InlineData(99, false)]
    public void IsSupported_OnlyAcceptsVersionsWithinRange(int version, bool expected)
    {
        Assert.Equal(expected, SettingsSchema.IsSupported(version));
    }

    [Fact]
    public void IsSupported_RejectsNewerVersions()
    {
        // 更新版本的程序写出的文件可能带未知字段，旧程序读过再保存会丢字段，
        // 所以必须拒绝，而不是"尽力而为地读"。
        Assert.False(SettingsSchema.IsSupported(SettingsSchema.CurrentVersion + 1));
    }

    [Fact]
    public void Range_CoversEveryVersionFromOldestToCurrent()
    {
        // 防止有人把 OldestSupportedVersion 往上调时忘了留迁移路径。
        for (var version = SettingsSchema.OldestSupportedVersion; version <= SettingsSchema.CurrentVersion; version++)
        {
            Assert.True(SettingsSchema.IsSupported(version), $"版本 {version} 应在支持范围内。");
        }
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void HasPasteBindings_StartsAtVersion5(int version, bool expected)
    {
        Assert.Equal(expected, SettingsSchema.HasPasteBindings(version));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void NeedsLegacyCustomMigration_OnlyVersion5(int version, bool expected)
    {
        Assert.Equal(expected, SettingsSchema.NeedsLegacyCustomMigration(version));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void HasUpdateSettings_StartsAtVersion7(int version, bool expected)
    {
        Assert.Equal(expected, SettingsSchema.HasUpdateSettings(version));
    }

    [Fact]
    public void CurrentVersion_MatchesStoreFormat()
    {
        // 防回归：Store 保存时写入的就是这个常量。改结构时忘了同步会让旧文件被判为"来自未来版本"。
        Assert.Equal(7, SettingsSchema.CurrentVersion);
    }
}
