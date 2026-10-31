namespace NMTools.App.Infrastructure;

/// <summary>
/// 主界面版本号标签的格式化。
///
/// 单独抽出来是为了能被单元测试覆盖：版本号曾经硬编码在 XAML 里，发版时只改了 csproj，
/// 界面就停留在旧版本上。现在统一从程序集元数据读取，格式化规则也要有测试兜底。
/// </summary>
public static class VersionLabel
{
    public const string PortableSuffix = "Portable";

    public static string Format(Version? version)
    {
        if (version is null)
        {
            return PortableSuffix;
        }

        // 第四段（Revision）在本文档中始终为 0，显示出来只会增加噪音。
        var display = version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        return $"v{display} {PortableSuffix}";
    }
}
