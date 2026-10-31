namespace NMTools.Core.Updates;

/// <summary>
/// 版本检查用例：取响应 → 解析清单 → 与当前版本比较。
///
/// 这个类不联网、不碰 UI、不写文件，所有失败都被收敛成 <see cref="UpdateCheckResult"/>，
/// 因此可以被单元测试穷举，也保证调用方不会因为一次检查失败而被打断。
/// </summary>
public sealed class UpdateChecker
{
    private readonly IReleaseFeed _feed;
    private readonly Version _currentVersion;

    public UpdateChecker(IReleaseFeed feed, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(currentVersion);
        _feed = feed;
        _currentVersion = ReleaseVersion.Normalize(currentVersion);
    }

    public Version CurrentVersion => _currentVersion;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ReleaseFeedResponse response;
        try
        {
            response = await _feed.GetLatestReleaseJsonAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(UpdateCheckFailureReason.Canceled);
        }
        catch (Exception)
        {
            // Feed 实现已经自行处理了已知异常；这里是最后一道兜底，
            // 保证任何意外都不会让版本检查冒泡到 UI 线程。
            return UpdateCheckResult.Failed(UpdateCheckFailureReason.Network);
        }

        if (!response.IsSuccess)
        {
            return UpdateCheckResult.Failed(response.FailureReason, response.HttpStatusCode);
        }

        var manifest = UpdateManifestParser.Parse(response.Json);
        if (manifest is null)
        {
            return UpdateCheckResult.Failed(UpdateCheckFailureReason.InvalidResponse);
        }

        return ReleaseVersion.IsNewer(manifest.Version, _currentVersion)
            ? UpdateCheckResult.Available(manifest)
            : UpdateCheckResult.UpToDate;
    }
}
