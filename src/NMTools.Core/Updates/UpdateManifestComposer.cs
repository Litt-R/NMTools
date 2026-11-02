namespace NMTools.Core.Updates;

/// <summary>
/// 把网页端点的三份文档组装成 <see cref="UpdateManifest"/>（见 ADR 0006）。
///
/// 与旧的 <c>UpdateManifestParser</c> 相比，下载地址不再来自响应正文：
/// 它由已校验的 tag 和 <see cref="UpdateManifest.ExpectedArchiveName"/> 推导，
/// 再经 <see cref="UpdateSource.IsTrustedReleaseUrl"/> 校验。这样即使上游文档被篡改，
/// 也无法把下载指向仓库外。
/// </summary>
public static class UpdateManifestComposer
{
    /// <summary>
    /// 组装清单。tag 不存在、不是稳定版或无法推导出受信下载地址时返回 null。
    /// </summary>
    public static UpdateManifest? Compose(ReleaseDocuments? documents)
    {
        if (documents?.TagName is not { Length: > 0 } tagName
            || !ReleaseVersion.TryParseStableTag(tagName, out var version))
        {
            return null;
        }

        string downloadUrl;
        try
        {
            downloadUrl = UpdateSource.ReleaseDownloadUrl(tagName);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!UpdateSource.IsTrustedReleaseUrl(downloadUrl))
        {
            return null;
        }

        var notes = ReleasesAtomParser.FindNotes(documents.AtomXml, tagName);
        var archiveName = UpdateManifest.ExpectedArchiveName(version);
        var asset = ExpandedAssetsParser.Find(documents.ExpandedAssetsHtml, archiveName);

        return new UpdateManifest(
            version,
            tagName,
            notes,
            downloadUrl,
            asset.SizeBytes)
        {
            // 优先用资产列表的摘要——它由上传时的文件内容算出，不需要人工维护。
            // 拿不到时退回发布说明里手写的哈希。注意这里用未截断的全文：
            // 摘要可能正好落在截断边界之后。
            ExpectedSha256 = asset.Sha256 ?? ReleaseNotesChecksum.Extract(
                ReleasesAtomParser.FindFullNotes(documents.AtomXml, tagName)),
        };
    }
}
