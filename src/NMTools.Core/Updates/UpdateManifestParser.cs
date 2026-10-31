using System.Text.Json;

namespace NMTools.Core.Updates;

/// <summary>
/// 解析 GitHub Releases API 的 <c>releases/latest</c> 响应。
///
/// 只读取展示和下载所需的字段，遇到结构不符合预期时返回失败而不是抛异常：
/// 上游是自己不可控的第三方 API，任何形状的响应都不应该让调用方崩溃。
/// </summary>
public static class UpdateManifestParser
{
    /// <summary>
    /// 解析响应正文。
    /// </summary>
    /// <returns>解析成功返回清单，结构不合法或无可用版本号时返回 null。</returns>
    public static UpdateManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // 草稿和预发布不属于稳定渠道，即使被 latest 返回也不采用。
            if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
            {
                return null;
            }

            var tagName = ReadString(root, "tag_name");
            if (!ReleaseVersion.TryParseStableTag(tagName, out var version))
            {
                return null;
            }

            var notes = ReadString(root, "body") ?? string.Empty;

            // 先提取哈希再截断说明，避免 64 位摘要正好落在截断处而丢掉。
            if (notes.Length > UpdateManifest.MaxNotesLength)
            {
                notes = notes[..UpdateManifest.MaxNotesLength];
            }

            var (downloadUrl, downloadSize, digest) = FindArchiveAsset(root, version);

            // 优先用 API 的 digest——它由上传时的文件内容算出，不需要人工维护。
            // 老版本 release 可能没有该字段，此时退回发布说明里手写的哈希。
            var expectedSha256 = digest ?? ReleaseNotesChecksum.Extract(ReadString(root, "body"));

            return new UpdateManifest(
                version,
                tagName!,
                notes,
                downloadUrl,
                downloadSize)
            {
                ExpectedSha256 = expectedSha256,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 找到与版本号匹配的便携包资产。只认 browser_download_url，不使用 API URL，
    /// 并要求资产名与发布脚本的命名规则一致，避免把其他产物当成便携包。
    /// </summary>
    private static (string? Url, long? SizeBytes, string? Digest) FindArchiveAsset(
        JsonElement root,
        Version version)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null, null);
        }

        var expectedName = UpdateManifest.ExpectedArchiveName(version);
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(asset, "name");
            if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = ReadString(asset, "browser_download_url");
            if (!UpdateSource.IsTrustedReleaseUrl(url))
            {
                continue;
            }

            long? size = asset.TryGetProperty("size", out var sizeElement)
                && sizeElement.ValueKind == JsonValueKind.Number
                && sizeElement.TryGetInt64(out var parsedSize)
                && parsedSize > 0
                    ? parsedSize
                    : null;

            return (url, size, ReadDigest(asset));
        }

        return (null, null, null);
    }

    /// <summary>
    /// 读取资产的 <c>digest</c> 字段，形如 <c>sha256:&lt;64 位十六进制&gt;</c>。
    /// GitHub 在 2025 年起为此字段提供值，与页面 "Assets" 里显示的一致。
    /// 只接受 sha256，其他算法或格式一律当作没有。
    /// </summary>
    private static string? ReadDigest(JsonElement asset)
    {
        var digest = ReadString(asset, "digest");
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = digest[prefix.Length..].Trim();
        return hash.Length == 64 && hash.All(char.IsAsciiHexDigit)
            ? hash.ToLowerInvariant()
            : null;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
