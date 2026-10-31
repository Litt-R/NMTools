using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class UpdateCheckerTests
{
    private const string NewerReleaseJson = """
    {
      "tag_name": "v0.7.0",
      "body": "更新说明",
      "draft": false,
      "prerelease": false,
      "html_url": "https://github.com/Litt-R/NMTools/releases/tag/v0.7.0",
      "assets": []
    }
    """;

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
        var checker = CreateChecker(new FakeFeed(ReleaseFeedResponse.Success(NewerReleaseJson)));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Equal(new Version(0, 7, 0, 0), result.Manifest!.Version);
    }

    [Fact]
    public async Task CheckAsync_SameVersion_ReportsUpToDate()
    {
        var checker = CreateChecker(new FakeFeed(ReleaseFeedResponse.Success(NewerReleaseJson)), 0, 7, 0);

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task CheckAsync_OlderVersion_ReportsUpToDate()
    {
        var checker = CreateChecker(new FakeFeed(ReleaseFeedResponse.Success(NewerReleaseJson)), 1, 0, 0);

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
    public async Task CheckAsync_MalformedJson_ReportsInvalidResponse()
    {
        var checker = CreateChecker(new FakeFeed(ReleaseFeedResponse.Success("{ not json")));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateCheckFailureReason.InvalidResponse, result.FailureReason);
    }

    [Fact]
    public async Task CheckAsync_PrereleaseLatest_ReportsInvalidResponseNotUpdate()
    {
        var json = NewerReleaseJson.Replace("\"prerelease\": false", "\"prerelease\": true");
        var checker = CreateChecker(new FakeFeed(ReleaseFeedResponse.Success(json)));

        var result = await checker.CheckAsync();

        // 预发布不应该被当成可用更新，也不该因为"没更新"而报错——这里按响应不可用处理。
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
        var checker = new UpdateChecker(new FakeFeed(ReleaseFeedResponse.Success(NewerReleaseJson)), new Version(0, 6, 1));

        Assert.Equal(new Version(0, 6, 1, 0), checker.CurrentVersion);
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        private readonly ReleaseFeedResponse _response;

        public FakeFeed(ReleaseFeedResponse response) => _response = response;

        public Task<ReleaseFeedResponse> GetLatestReleaseJsonAsync(CancellationToken cancellationToken = default) =>
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

        public Task<ReleaseFeedResponse> GetLatestReleaseJsonAsync(CancellationToken cancellationToken = default)
        {
            if (!_ignoreToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromException<ReleaseFeedResponse>(_exception);
        }
    }
}
