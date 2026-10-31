namespace NMTools.Core.Updates;

public enum UpdateCheckStatus
{
    /// <summary>当前已是最新稳定版。</summary>
    UpToDate,

    /// <summary>发现更高的稳定版。</summary>
    UpdateAvailable,

    /// <summary>检查失败。具体原因只用于日志分类，不对用户展示。</summary>
    Failed,
}

/// <summary>检查失败的原因，仅用于诊断日志分类，绝不包含响应正文。</summary>
public enum UpdateCheckFailureReason
{
    None,

    /// <summary>网络不可达、DNS 失败、连接被重置等。</summary>
    Network,

    /// <summary>请求超时。</summary>
    Timeout,

    /// <summary>HTTP 非成功状态码，包括 403/429 速率限制。</summary>
    HttpStatus,

    /// <summary>响应不是合法 JSON，或结构不符合预期。</summary>
    InvalidResponse,

    /// <summary>调用方取消（例如程序退出）。</summary>
    Canceled,
}

public sealed record UpdateCheckResult
{
    private UpdateCheckResult(
        UpdateCheckStatus status,
        UpdateManifest? manifest,
        UpdateCheckFailureReason failureReason,
        int? httpStatusCode)
    {
        Status = status;
        Manifest = manifest;
        FailureReason = failureReason;
        HttpStatusCode = httpStatusCode;
    }

    public UpdateCheckStatus Status { get; }

    /// <summary>仅在 <see cref="UpdateCheckStatus.UpdateAvailable"/> 时有值。</summary>
    public UpdateManifest? Manifest { get; }

    public UpdateCheckFailureReason FailureReason { get; }

    /// <summary>失败时的 HTTP 状态码；网络层失败或解析失败时为 null。</summary>
    public int? HttpStatusCode { get; }

    public static UpdateCheckResult UpToDate { get; } =
        new(UpdateCheckStatus.UpToDate, null, UpdateCheckFailureReason.None, null);

    public static UpdateCheckResult Available(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, manifest, UpdateCheckFailureReason.None, null);
    }

    public static UpdateCheckResult Failed(UpdateCheckFailureReason reason, int? httpStatusCode = null)
    {
        return new UpdateCheckResult(UpdateCheckStatus.Failed, null, reason, httpStatusCode);
    }

    /// <summary>
    /// 面向用户的失败提示。所有失败原因共用一句可操作的文案，
    /// 不暴露状态码、异常信息或响应内容。
    /// </summary>
    public const string FailureMessage = "检查失败，可稍后重试";
}
