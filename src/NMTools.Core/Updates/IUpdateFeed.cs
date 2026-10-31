namespace NMTools.Core.Updates;

/// <summary>
/// 获取更新源响应的抽象。Core 只负责"拿到正文后怎么判断"，
/// HTTP 调用由 <c>NMTools.App</c> 实现，便于用假实现测试各种响应形状。
/// </summary>
public interface IReleaseFeed
{
    /// <summary>
    /// 拉取最新发布的原始 JSON 正文。失败时返回带有原因的结果，不抛异常。
    /// </summary>
    Task<ReleaseFeedResponse> GetLatestReleaseJsonAsync(CancellationToken cancellationToken = default);
}

/// <param name="Json">响应正文，只在内存中用于解析，不得写入日志。</param>
public sealed record ReleaseFeedResponse(string? Json, UpdateCheckFailureReason FailureReason, int? HttpStatusCode)
{
    public bool IsSuccess => Json is not null && FailureReason == UpdateCheckFailureReason.None;

    public static ReleaseFeedResponse Success(string json) =>
        new(json, UpdateCheckFailureReason.None, null);

    public static ReleaseFeedResponse Failure(UpdateCheckFailureReason reason, int? httpStatusCode = null) =>
        new(null, reason, httpStatusCode);
}
