using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class UpdateCheckerTests
{
    private const string AtomXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <entry>
        <link rel="alternate" href="https://github.com/Litt-R/NMTools/releases/tag/v0.7.0"/>
        <content type="html">更新说明</content>
      </entry>
    </feed>
    """;

    /// <summary>最新稳定版 v0.7.0 的三份文档。</summary>
    private static ReleaseFeedResponse NewerRelease() =>
        ReleaseFeedResponse.Success(new ReleaseDocuments("v0.7.0", AtomXml, null));

    private static UpdateChecker CreateChecker(
        IReleaseFeed feed,
        int major = 0,
        int minor = 6,
        int build = 1)
    {
        return new UpdateChecker(feed, new Version(major, minor, build, 0));
    }

    [Fact]
    public async Task CheckAsync_NewerStableVersion_ReportsAvailable()
    {
        var checker = CreateChecker(new FakeFeed(NewerRelease()));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Equal(new Version(0, 7, 0, 0), result.Manifest!.Version);
    }

    [Fact]
    public async Task CheckAsync_SameVersion_ReportsUpToDate()
    {
        var checker = CreateChecker(new FakeFeed(NewerRelease()), 0, 7, 0);

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task CheckAsync_OlderVersion_ReportsUpToDate()
    {
        var checker = CreateChecker(new FakeFeed(NewerRelease()), 1, 0, 0);

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Theory]
    [InlineData(UpdateCheckFailureReason.Network)]
    [InlineData(UpdateCheckFailureReason.Timeout)]
    [InlineData(UpdateCheckFailureReason.InvalidResponse)]
    [InlineData(UpdateCheckFailureReason.Canceled)]
    public async Task CheckAsync_FeedFailure_ReportsFailedWithSameReason(UpdateCheckFailureReason reason)
    {
        var checker = CreateChecker(new FakeFeed(ReleaseFeedResponse.Failure(reason)));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(reason, result.FailureReason);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task CheckAsync_RateLimit_ReportsHttpStatusAndMessage()
    {
        var checker = CreateChecker(new FakeFeed(ReleaseFeedResponse.Failure(
            UpdateCheckFailureReason.HttpStatus,
            403)));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureReason.HttpStatus, result.FailureReason);
        Assert.Equal(403, result.HttpStatusCode);
        // 用户可见文案对所有失败一致，不暴露状态码。
        Assert.Equal("检查失败，可稍后重试", UpdateCheckResult.FailureMessage);
    }

    [Fact]
    public async Task CheckAsync_NoUsableTag_ReportsInvalidResponse()
    {
        // 上游没有最新稳定版时，feed 不会给出 tag，组装阶段判为响应无效。
        var checker = CreateChecker(new FakeFeed(
            ReleaseFeedResponse.Success(new ReleaseDocuments(null, null, null))));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureReason.InvalidResponse, result.FailureReason);
    }

    [Fact]
    public async Task CheckAsync_PrereleaseTag_IsNeverReportedAsUpdate()
    {
        // 预发布 tag 不能当成可用更新（ADR 0006 最关心的回归点）。
        var checker = CreateChecker(new FakeFeed(
            ReleaseFeedResponse.Success(new ReleaseDocuments("v0.7.0-beta.1", AtomXml, null))));

        var result = await checker.CheckAsync();

        // 既不报"有更新"，也不该报成"已是最新"——按响应不可用处理。
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureReason.InvalidResponse, result.FailureReason);
    }

    [Fact]
    public async Task CheckAsync_FeedThrows_IsContained()
    {
        var checker = CreateChecker(new ThrowingFeed(new InvalidOperationException("boom")));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureReason.Network, result.FailureReason);
    }

    [Fact]
    public async Task CheckAsync_Cancellation_ReportsCanceled()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var checker = CreateChecker(new ThrowingFeed(new OperationCanceledException(), ignoreToken: true));

        var result = await checker.CheckAsync(source.Token);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureReason.Canceled, result.FailureReason);
    }

    [Fact]
    public void CurrentVersion_IsNormalized()
    {
        var checker = new UpdateChecker(new FakeFeed(NewerRelease()), new Version(0, 6, 1));

        Assert.Equal(new Version(0, 6, 1, 0), checker.CurrentVersion);
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        private readonly ReleaseFeedResponse _response;

        public FakeFeed(ReleaseFeedResponse response) => _response = response;

        public Task<ReleaseFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_response);
    }

    private sealed class ThrowingFeed : IReleaseFeed
    {
        private readonly Exception _exception;
        private readonly bool _ignoreToken;

        public ThrowingFeed(Exception exception, bool ignoreToken = false)
        {
            _exception = exception;
            _ignoreToken = ignoreToken;
        }

        public Task<ReleaseFeedResponse> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            if (!_ignoreToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromException<ReleaseFeedResponse>(_exception);
        }
    }
}
