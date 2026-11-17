using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NMTools.Core.Tests;

/// <summary>
/// 发版一致性守卫。
///
/// 版本号写在两个 csproj 里，发版时靠人工同步。0.7.2 发布过程中暴露过这个风险：
/// 主程序与更新器版本不一致时，更新器会以错误的身份替换自己；
/// 代码版本与 tag 不一致时，用户装上的包和发布页声称的版本对不上。
///
/// 这些检查放在这里，是因为它们不需要联网、不需要跑起来，却能在发版前就拦住错误。
/// </summary>
public sealed class ReleaseVersionConsistencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void AppAndUpdater_DeclareTheSameVersion()
    {
        var app = ReadVersions("src/NMTools.App/NMTools.App.csproj");
        var updater = ReadVersions("src/NMTools.Updater/NMTools.Updater.csproj");

        Assert.Equal(app.Version, updater.Version);
        Assert.Equal(app.AssemblyVersion, updater.AssemblyVersion);
        Assert.Equal(app.FileVersion, updater.FileVersion);
    }

    [Theory]
    [InlineData("src/NMTools.App/NMTools.App.csproj")]
    [InlineData("src/NMTools.Updater/NMTools.Updater.csproj")]
    public void VersionFields_AreConsistentWithinAProject(string relativePath)
    {
        var versions = ReadVersions(relativePath);

        // <Version> 是四段补零后的前缀，例如 0.7.2 与 0.7.2.0。
        Assert.StartsWith(versions.Version + ".", versions.FileVersion.TrimEnd('0').TrimEnd('.') + ".");
        Assert.StartsWith(versions.Version + ".", versions.AssemblyVersion.TrimEnd('0').TrimEnd('.') + ".");
    }

    [Fact]
    public void Version_IsAStableThreePartVersion()
    {
        // 稳定渠道只接受 major.minor.build，带 -beta 之类的 tag 不会被推送，
        // 版本号本身也不应出现预发布后缀。
        var versions = ReadVersions("src/NMTools.App/NMTools.App.csproj");

        Assert.Matches(@"^\d+\.\d+\.\d+$", versions.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", versions.AssemblyVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", versions.FileVersion);
    }

    [Fact]
    public void PublishScript_DerivesArchiveNameFromFileVersion()
    {
        // 发布脚本从 exe 的 FileVersion 推导 zip 名，必须是三段式，
        // 否则会产出 NMTools-v0.7.2.0-win-x64.zip，与程序推导的下载地址不符。
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "publish-portable.ps1"));

        Assert.Contains("VersionInfo.FileVersion", script);
        Assert.Contains(@"^(\d+\.\d+\.\d+)", script);
    }

    [Fact]
    public void ExpectedArchiveName_MatchesPublishScriptConvention()
    {
        // 程序侧推导的下载地址必须和发布脚本生成的 zip 名一致，否则更新会 404。
        var name = NMTools.Core.Updates.UpdateManifest.ExpectedArchiveName(new Version(0, 7, 2));

        Assert.Equal("NMTools-v0.7.2-win-x64.zip", name);
    }

    private static (string Version, string AssemblyVersion, string FileVersion) ReadVersions(string relativePath)
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot, relativePath));
        var propertyGroup = document.Root!
            .Elements("PropertyGroup")
            .FirstOrDefault(group => group.Element("Version") is not null)
            ?? throw new InvalidOperationException($"{relativePath} 里没有包含 <Version> 的 PropertyGroup。");

        return (
            Read(propertyGroup, "Version"),
            Read(propertyGroup, "AssemblyVersion"),
            Read(propertyGroup, "FileVersion"));
    }

    private static string Read(XElement propertyGroup, string name)
    {
        var element = propertyGroup.Element(name)
            ?? throw new InvalidOperationException($"缺少 <{name}>。");
        var value = element.Value.Trim();
        Assert.False(string.IsNullOrEmpty(value), $"<{name}> 为空。");
        return value;
    }

    /// <summary>
    /// 从测试程序集所在目录向上找到含 NMTools.sln 的目录，避免把绝对路径写死在测试里。
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NMTools.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（没有 NMTools.sln）。");
    }
}
