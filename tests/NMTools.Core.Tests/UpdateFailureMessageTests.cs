using NMTools.App.Services;
using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

/// <summary>
/// 更新失败时用户会看到哪一句话。
///
/// 这组测试来自一次真实事故：0.7.1 把发布页面上四舍五入的 "69.9 MB" 当精确大小比较，
/// 于是每一次正常下载都弹出"下载的更新包大小不正确"。用户看到的是这句错误提示，
/// 而真正的原因（近似值被当精确值）在日志里。
///
/// 因此这里断言的重点不是文案本身，而是**哪些情况根本不该产生用户可见的错误**：
/// 拿不到摘要属于网络降级，必须继续更新，不能弹任何失败提示。
/// </summary>
public sealed class UpdateFailureMessageTests
{
    [Fact]
    public void MissingDigestBecauseAssetListWasUnreachable_IsNotAUserFacingFailure()
    {
        // ADR 0006：资产列表拿不到只是降级。用户不应该因此看到任何错误。
        var manifest = CreateManifest(sha256: null, status: AssetLookupStatus.PageMissing);

        Assert.Null(manifest.ExpectedSha256);
        Assert.Equal(AssetLookupStatus.PageMissing, manifest.AssetStatus);
        Assert.True(manifest.SupportsAutomaticUpdate, "拿不到摘要时仍应允许自动更新。");
    }

    [Theory]
    [InlineData(AssetLookupStatus.AssetNotFound)]
    [InlineData(AssetLookupStatus.DigestMissing)]
    public void MissingDigestBecauseParserOutOfDate_StillAllowsUpdate(AssetLookupStatus status)
    {
        // 解析器失效时同样不能把用户挡在门外，只是日志里会留下 ERROR。
        var manifest = CreateManifest(sha256: null, status: status);

        Assert.Null(manifest.ExpectedSha256);
        Assert.True(manifest.SupportsAutomaticUpdate);
    }

    [Fact]
    public void SizeIsDisplayOnlyAndNeverBlocksAnUpdate()
    {
        // 页面显示 69.9 MB，换算后与真实文件相差 43194 字节。
        // 这个字段现在只用于界面显示和进度条，不能作为失败依据。
        var manifest = CreateManifest(
            sha256: "9a124a5dca5756f99cd29e455e75e489eb6b4ff3b672f6addb0d3e0f2bd046ac",
            status: AssetLookupStatus.Resolved,
            declaredSize: 73_295_462);

        Assert.True(manifest.SupportsAutomaticUpdate);
        Assert.Equal(73_295_462, manifest.DownloadSizeBytes);
        // 摘要存在，说明这次会真正校验内容；体积不参与判断。
        Assert.NotNull(manifest.ExpectedSha256);
    }

    [Fact]
    public void ResolvedDigestMeansContentWillActuallyBeVerified()
    {
        var manifest = CreateManifest(
            sha256: "9a124a5dca5756f99cd29e455e75e489eb6b4ff3b672f6addb0d3e0f2bd046ac",
            status: AssetLookupStatus.Resolved);

        Assert.Equal(AssetLookupStatus.Resolved, manifest.AssetStatus);
        Assert.False(manifest.AssetStatus is AssetLookupStatus.PageMissing);
    }

    [Fact]
    public void EveryFailureReasonHasAUserFacingMessage()
    {
        // 枚举新增值时，这里会失败，提醒补一条用户能看懂的话，
        // 而不是让用户看到空字符串或内部枚举名。
        foreach (var reason in Enum.GetValues<PrepareFailureReason>())
        {
            if (reason == PrepareFailureReason.None)
            {
                continue;
            }

            var message = UpdateService.DescribePrepareFailureForTesting(reason);
            Assert.False(string.IsNullOrWhiteSpace(message), $"{reason} 没有用户可见的说明。");
            Assert.DoesNotContain(reason.ToString(), message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SizeMismatchMessageNoLongerClaimsThePackageSizeIsWrong()
    {
        // 现在这个枚举只在"超过 200 MB 上限"时触发，文案不能再说是大小不正确，
        // 否则会再次误导用户去怀疑发布包本身。
        var message = UpdateService.DescribePrepareFailureForTesting(PrepareFailureReason.SizeMismatch);

        Assert.DoesNotContain("大小不正确", message);
        Assert.Contains("大小异常", message);
    }

    [Fact]
    public void ChecksumMismatchTellsTheUserThePackageWasDeleted()
    {
        var message = UpdateService.DescribePrepareFailureForTesting(PrepareFailureReason.ChecksumMismatch);

        Assert.Contains("已删除", message);
        Assert.Contains("重试", message);
    }

    private static UpdateManifest CreateManifest(
        string? sha256,
        AssetLookupStatus status,
        long? declaredSize = null)
    {
        return new UpdateManifest(
            new Version(0, 7, 2),
            "v0.7.2",
            Notes: string.Empty,
            DownloadUrl: "https://github.com/Litt-R/NMTools/releases/download/v0.7.2/NMTools-v0.7.2-win-x64.zip",
            DownloadSizeBytes: declaredSize)
        {
            ExpectedSha256 = sha256,
            AssetStatus = status,
        };
    }
}
