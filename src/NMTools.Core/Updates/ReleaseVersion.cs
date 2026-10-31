namespace NMTools.Core.Updates;

/// <summary>
/// 版本号规则：只接受稳定版 tag，并按四段版本比较。
///
/// 用 <see cref="Version"/> 而不是字符串比较，避免 "0.6.10" 被判断成比 "0.6.9" 更旧。
/// 统一补齐到 Major.Minor.Build.Revision，避免 <c>Version(0, 6, 1)</c> 与 <c>Version(0, 6, 1, 0)</c>
/// 在比较时被判为不相等（未设置的 Build/Revision 是 -1）。
/// </summary>
public static class ReleaseVersion
{
    /// <summary>
    /// 补齐缺失的版本段。<see cref="Version"/> 未设置的段为 -1，直接比较会让 0.6.1 小于 0.6.1.0。
    /// </summary>
    public static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Version(
            version.Major,
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    /// <summary>
    /// 把 release tag 解析为稳定版版本号。允许可选的 "v" 前缀；预发布后缀（-rc1）、
    /// 渠道标记或其他非数字内容一律拒绝——更新只走稳定渠道。
    /// </summary>
    public static bool TryParseStableTag(string? tagName, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        var text = tagName.Trim();
        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        // "0.7.0-beta.1"、"v0.7.0+build" 和 "nightly" 都不属于稳定版渠道。
        if (text.Length == 0 || text.Any(character => !char.IsAsciiDigit(character) && character != '.'))
        {
            return false;
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    public static bool IsNewer(Version candidate, Version current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);
        return Normalize(candidate).CompareTo(Normalize(current)) > 0;
    }
}
