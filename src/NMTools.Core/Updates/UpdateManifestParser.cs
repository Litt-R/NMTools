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
            if (notes.Length > UpdateManifest.MaxNotesLength)
            {
                notes = notes[..UpdateManifest.MaxNotesLength];
            }

            var (downloadUrl, downloadSize) = FindArchiveAsset(root, version);
            var checksumUrl = FindChecksumAsset(root);
            var releasePageUrl = ResolveReleasePageUrl(root, tagName!);

            return new UpdateManifest(
                version,
                tagName!,
                notes,
                downloadUrl,
                downloadSize,
                releasePageUrl,
                checksumUrl);
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
    private static (string? Url, long? SizeBytes) FindArchiveAsset(JsonElement root, Version version)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
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

            return (url, size);
        }

        return (null, null);
    }

    /// <summary>
    /// 找到校验文件资产。<c>SHA256SUMS.txt</c> 是发布脚本固定产出的名字。
    /// </summary>
    private static string? FindChecksumAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(asset, "name");
            if (!string.Equals(name, ChecksumManifest.AssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = ReadString(asset, "browser_download_url");
            return UpdateSource.IsTrustedReleaseUrl(url) ? url : null;
        }

        return null;
    }

    private static string ResolveReleasePageUrl(JsonElement root, string tagName)
    {
        var htmlUrl = ReadString(root, "html_url");
        if (UpdateSource.IsTrustedReleaseUrl(htmlUrl))
        {
            return htmlUrl!;
        }

        // 不使用 API 响应里不受信任的链接兜底，直接按仓库常量和 tag 拼一个确定的地址。
        return UpdateSource.TagPageUrl(tagName);
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
