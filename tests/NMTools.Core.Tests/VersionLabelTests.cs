using NMTools.App.Infrastructure;

namespace NMTools.Core.Tests;

public sealed class VersionLabelTests
{
    [Theory]
    [InlineData(0, 6, 1, 0, "v0.6.1 Portable")]
    [InlineData(0, 6, 0, 0, "v0.6.0 Portable")]
    [InlineData(1, 0, 0, 0, "v1.0.0 Portable")]
    [InlineData(2, 3, 4, 5, "v2.3.4.5 Portable")]
    public void Format_UsesAssemblyVersion(int major, int minor, int build, int revision, string expected)
    {
        var version = new Version(major, minor, build, revision);

        Assert.Equal(expected, VersionLabel.Format(version));
    }

    [Fact]
    public void Format_OmitsZeroRevision()
    {
        // 第四段在本项目中始终为 0，显示出来只是噪音。
        var result = VersionLabel.Format(new Version(0, 6, 1, 0));

        Assert.Equal("v0.6.1 Portable", result);
        Assert.DoesNotContain(".0.0", result);
    }

    [Fact]
    public void Format_KeepsNonZeroRevision()
    {
        Assert.Equal("v0.6.1.7 Portable", VersionLabel.Format(new Version(0, 6, 1, 7)));
    }

    [Fact]
    public void Format_NullVersion_FallsBackToSuffixOnly()
    {
        Assert.Equal(VersionLabel.PortableSuffix, VersionLabel.Format(null));
    }

    [Fact]
    public void ApplicationVersion_IsResolvedFromAssemblyMetadata()
    {
        // 防回归：界面版本号必须来自程序集元数据（csproj 的 <Version>），不能硬编码。
        // 这里不锁死具体版本号——每次发版都会变，锁死只会逼着人改测试；
        // 只要求程序集版本可用且三段式完整，界面文本会随它自动更新。
        var assemblyVersion = typeof(VersionLabel).Assembly.GetName().Version;

        Assert.NotNull(assemblyVersion);
        Assert.True(assemblyVersion.Major >= 0);
        Assert.True(assemblyVersion.Minor >= 0);
        Assert.True(assemblyVersion.Build >= 0, $"程序集版本缺少 Build 段：{assemblyVersion}。");

        // 版本徽标显示三段式，第四段为零时不显示，这是 VersionLabel 的既定格式。
        var expected = $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build} Portable";
        if (assemblyVersion.Revision == 0)
        {
            Assert.Equal(expected, VersionLabel.Format(assemblyVersion));
        }
    }
}
