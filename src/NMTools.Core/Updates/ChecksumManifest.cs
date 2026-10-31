using System.Globalization;

namespace NMTools.Core.Updates;

/// <summary>
/// 校验文件清单（<c>SHA256SUMS.txt</c>）的解析与查找。
///
/// 采用 <c>sha256sum</c> 的通用格式：每行 <c>&lt;64 位十六进制&gt;  &lt;文件名&gt;</c>，
/// 允许文件名前有 <c>*</c>（二进制模式标记）。只按文件名匹配，不解析路径，
/// 避免清单里写相对路径导致查找错位。
/// </summary>
public static class ChecksumManifest
{
    /// <summary>Release 里校验文件的固定资产名。</summary>
    public const string AssetName = "SHA256SUMS.txt";

    /// <summary>
    /// 解析清单文本，返回「文件名 → 小写十六进制 SHA-256」的映射。
    /// 格式不正确的行被忽略而不是让整份解析失败——多一个无关文件不该导致更新不可用。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // 十六进制部分固定 64 字符，后面是空白和文件名。
            if (line.Length < 64 + 2)
            {
                continue;
            }

            var hashPart = line[..64];
            if (!IsHex(hashPart))
            {
                continue;
            }

            var remainder = line[64..].TrimStart();
            if (remainder.Length == 0)
            {
                continue;
            }

            // 二进制模式标记：哈希与文件名之间是 " *name"。
            if (remainder[0] == '*')
            {
                remainder = remainder[1..];
            }

            var fileName = Path.GetFileName(remainder.Trim());
            if (fileName.Length == 0)
            {
                continue;
            }

            result[fileName] = hashPart.ToLowerInvariant();
        }

        return result;
    }

    /// <summary>
    /// 在清单里查找指定文件名的校验值。找不到时返回 null。
    /// </summary>
    public static string? FindChecksum(IReadOnlyDictionary<string, string> manifest, string fileName)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return manifest.TryGetValue(Path.GetFileName(fileName), out var hash) ? hash : null;
    }

    /// <summary>
    /// 不区分大小写地比较两个十六进制摘要。
    /// </summary>
    public static bool Matches(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        return string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 生成一行清单条目，供发布脚本参考格式。保留在 Core 是为了让两边格式一致。
    /// </summary>
    public static string FormatEntry(string hash, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return $"{hash.ToLowerInvariant()}  {fileName}";
    }
}
