using NMTools.App.Services;
using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

/// <summary>
/// UpdateService 的交互规则：自动检查必须完全静默，手动检查才有反馈。
/// 用假的 HTTP 处理器代替真实网络，不联网。
/// </summary>
public sealed class UpdateServiceTests
{
    private const string NewerReleaseJson = """
    {
      "tag_name": "v9.9.9",
      "body": "更新说明",
      "draft": false,
      "prerelease": false,
      "html_url": "https://github.com/Litt-R/NMTools/releases/tag/v9.9.9",
      "assets": []
    }
    """;

    [Fact]
    public async Task CheckAsync_AutomaticAndUpdateAvailable_PromptsOnce()
    {
        using var service = CreateService(NewerReleaseJson, out var alerts, out var prompts);

        await service.CheckAsync(interactive: false);

        // 自动检查唯一会打扰用户的情况：确实有新版本。
        Assert.Single(prompts);
        Assert.Equal(new Version(9, 9, 9, 0), prompts[0].Manifest.Version);
        Assert.Equal(new Version(1, 0, 0, 0), prompts[0].CurrentVersion);
        Assert.Empty(alerts);
    }

    [Fact]
    public async Task CheckAsync_AutomaticAndUpToDate_IsSilent()
    {
        using var service = CreateService("{\"tag_name\":\"v1.0.0\"}", out var alerts, out var prompts);

        await service.CheckAsync(interactive: false);

        Assert.Empty(alerts);
        Assert.Empty(prompts);
    }

    [Fact]
    public async Task CheckAsync_AutomaticAndFailure_IsSilent()
    {
        using var service = CreateService("not json", out var alerts, out var prompts);

        await service.CheckAsync(interactive: false);

        // 启动检查失败不能让用户看到任何弹窗。
        Assert.Empty(alerts);
        Assert.Empty(prompts);
    }

    [Fact]
    public async Task CheckAsync_ManualAndUpToDate_TellsUser()
    {
        using var service = CreateService("{\"tag_name\":\"v1.0.0\"}", out var alerts, out _);

        await service.CheckAsync(interactive: true);

        Assert.Single(alerts);
        Assert.Contains("已是最新版本", alerts[0].Message);
    }

    [Fact]
    public async Task CheckAsync_ManualAndFailure_ShowsGenericMessage()
    {
        using var service = CreateService("not json", out var alerts, out _);

        await service.CheckAsync(interactive: true);

        Assert.Single(alerts);
        Assert.Equal(UpdateCheckResult.FailureMessage, alerts[0].Message);
        // 用户可见文案不得泄露状态码或响应内容。
        Assert.DoesNotContain("json", alerts[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("400", alerts[0].Message);
    }

    [Fact]
    public async Task CheckAsync_HttpError_IsSilentWhenAutomatic()
    {
        using var service = CreateService(
            null,
            out var alerts,
            out var prompts,
            statusCode: System.Net.HttpStatusCode.Forbidden);

        await service.CheckAsync(interactive: false);

        Assert.Empty(alerts);
        Assert.Empty(prompts);
    }

    [Fact]
    public async Task CheckAsync_ConcurrentCalls_OnlyOneRuns()
    {
        // 在请求真正返回前发起第二次检查：第一次还在飞行中，第二次必须被直接丢弃。
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var service = CreateService(
            NewerReleaseJson,
            out _,
            out var prompts,
            asyncRespond: async () =>
            {
                entered.SetResult();
                await release.Task;
            });

        var first = service.CheckAsync(interactive: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.IsBusy);
        var second = service.CheckAsync(interactive: false);
        release.SetResult();
        await Task.WhenAll(first, second);

        // 第二次调用被直接返回，不会产生第二个弹窗。
        Assert.Single(prompts);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task CheckAsync_HijackedReleasePageUrl_IsReplacedWithRepositoryUrl()
    {
        // 即使清单里的链接被篡改，交给对话框的地址也必须属于本仓库。
        var hijacked = NewerReleaseJson.Replace(
            "https://github.com/Litt-R/NMTools/releases/tag/v9.9.9",
            "https://evil.example.com/payload");
        using var service = CreateService(hijacked, out _, out var prompts);

        await service.CheckAsync(interactive: false);

        Assert.Single(prompts);
        Assert.DoesNotContain("evil.example.com", prompts[0].Manifest.ReleasePageUrl);
        Assert.True(UpdateSource.IsTrustedReleaseUrl(prompts[0].Manifest.ReleasePageUrl));
    }

    private static UpdateService CreateService(
        string? json,
        out List<(string Message, string Title, bool IsWarning)> alerts)
    {
        return CreateService(json, out alerts, out _);
    }

    private static UpdateService CreateService(
        string? json,
        out List<(string Message, string Title, bool IsWarning)> alerts,
        out List<(UpdateManifest Manifest, Version CurrentVersion)> prompts,
        System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK,
        Func<Task>? asyncRespond = null)
    {
        var recordedAlerts = new List<(string Message, string Title, bool IsWarning)>();
        var recordedPrompts = new List<(UpdateManifest Manifest, Version CurrentVersion)>();
        alerts = recordedAlerts;
        prompts = recordedPrompts;
        var handler = new StubHandler(async () =>
        {
            if (asyncRespond is not null)
            {
                await asyncRespond();
            }

            return new System.Net.Http.HttpResponseMessage(statusCode)
            {
                Content = new System.Net.Http.StringContent(
                    json ?? string.Empty,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });

        return new UpdateService(
            new Version(1, 0, 0, 0),
            () => null,
            log: null,
            showAlert: (message, title, isWarning, _) => recordedAlerts.Add((message, title, isWarning)),
            handler: handler,
            promptForUpdate: (manifest, currentVersion, _) =>
            {
                recordedPrompts.Add((manifest, currentVersion));
                return false;
            });
    }

    private sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<Task<System.Net.Http.HttpResponseMessage>> _respond;

        public StubHandler(Func<Task<System.Net.Http.HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _respond();
        }
    }
}
