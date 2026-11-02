namespace NMTools.Core.Updates;

/// <summary>
/// 更新检查所需的三份原始文档（见 ADR 0006）。字段都可能在失败时为 null，
/// 由 <see cref="UpdateManifestComposer"/> 统一判断。
/// </summary>
/// <param name="TagName">
/// 从 <c>releases/latest</c> 的 302 <c>Location</c> 取出并已校验的稳定版 tag。
/// 为 null 表示上游没有可用的最新稳定版。
/// </param>
/// <param name="AtomXml">整份 releases Atom 源，用于按 tag 查找更新说明。</param>
/// <param name="ExpandedAssetsHtml">该 tag 的资产列表片段，用于取资产名、体积和摘要。</param>
public sealed record ReleaseDocuments(
    string? TagName,
    string? AtomXml,
    string? ExpandedAssetsHtml);

/// <summary>
/// 获取更新源文档的抽象。Core 只负责"拿到文档后怎么判断"，
/// HTTP 调用由 <c>NMTools.App</c> 实现，便于用假实现测试各种响应形状。
/// </summary>
public interface IReleaseFeed
{
    /// <summary>
    /// 依次拉取最新稳定版 tag、Atom 源和资产列表。失败时返回带有原因的结果，不抛异常。
    /// </summary>
    Task<ReleaseFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
}

/// <param name="Documents">原始文档，只在内存中用于解析，不得写入日志。</param>
public sealed record ReleaseFeedResponse(
    ReleaseDocuments? Documents,
    UpdateCheckFailureReason FailureReason,
    int? HttpStatusCode)
{
    public bool IsSuccess => Documents is not null && FailureReason == UpdateCheckFailureReason.None;

    public static ReleaseFeedResponse Success(ReleaseDocuments documents) =>
        new(documents, UpdateCheckFailureReason.None, null);

    public static ReleaseFeedResponse Failure(UpdateCheckFailureReason reason, int? httpStatusCode = null) =>
        new(null, reason, httpStatusCode);
}
