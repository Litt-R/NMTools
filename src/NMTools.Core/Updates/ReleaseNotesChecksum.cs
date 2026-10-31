using System.Text.RegularExpressions;

namespace NMTools.Core.Updates;

/// <summary>
/// 从发布说明正文里提取声明的 SHA-256。
///
/// 约定的写法（大小写不敏感，允许 Markdown 强调标记）：
/// <code>
/// SHA256: 89b1e7728396c300d631b4d85241a441a9adf33e3657cba7009c2727d6e73b2d
/// SHA-256：`89b1e7...`      （全角冒号也可以）
/// </code>
///
/// 这是可选的：没有就跳过校验，不因为发布说明格式不合规而让更新失败。
/// </summary>
public static partial class ReleaseNotesChecksum
{
    /// <summary>
    /// 从正文中提取第一个 64 位十六进制摘要。找不到或格式不对时返回 null。
    /// </summary>
    public static string? Extract(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var match = ChecksumPattern().Match(notes);
        return match.Success ? match.Groups["hash"].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 校验哈希是否相等，大小写不敏感。任一为空时返回 false——宁可报告"无校验值"，
    /// 也不要让空值意外通过。
    /// </summary>
    public static bool Matches(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        return string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"sha-?256\s*[:：]\s*\**\s*`?(?<hash>[0-9a-fA-F]{64})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumPattern();
}
