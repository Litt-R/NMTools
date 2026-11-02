using NMTools.App.Services;
using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

/// <summary>
/// UpdateService 的交互规则：自动检查必须完全静默，手动检查才有反馈。
/// 用假的 HTTP 处理器代替真实网络，不联网。
/// </summary>
public sealed class UpdateServiceTests
{
    private const string NewerTag = "v9.9.9";

    private const string NewerReleaseAtom = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <entry>
        <link rel="alternate" href="https://github.com/Litt-R/NMTools/releases/tag/v9.9.9"/>
        <content type="html">更新说明</content>
      </entry>
    </feed>
    """;

    [Fact]
    public async Task CheckAsync_AutomaticAndUpdateAvailable_PromptsOnce()
    {
        using var service = CreateService(NewerTag, out var alerts, out var prompts);

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
        using var service = CreateService("v1.0.0", out var alerts, out var prompts);

        await service.CheckAsync(interactive: false);

        Assert.Empty(alerts);
        Assert.Empty(prompts);
    }

    [Fact]
    public async Task CheckAsync_AutomaticAndFailure_IsSilent()
    {
        using var service = CreateService(null, out var alerts, out var prompts);

        await service.CheckAsync(interactive: false);

        // 启动检查失败不能让用户看到任何弹窗。
        Assert.Empty(alerts);
        Assert.Empty(prompts);
    }

    [Fact]
    public async Task CheckAsync_ManualAndUpToDate_TellsUser()
    {
        using var service = CreateService("v1.0.0", out var alerts, out _);

        await service.CheckAsync(interactive: true);

        Assert.Single(alerts);
        Assert.Contains("已是最新版本", alerts[0].Message);
    }

    [Fact]
    public async Task CheckAsync_ManualAndFailure_ShowsGenericMessage()
    {
        using var service = CreateService(null, out var alerts, out _);

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
        var signalled = 0;
        using var service = CreateService(
            NewerTag,
            out _,
            out var prompts,
            asyncRespond: async () =>
            {
                // 一次检查会发多个请求，只让第一个请求停下来等"放行"信号。
                if (Interlocked.Exchange(ref signalled, 1) == 0)
                {
                    entered.SetResult();
                    await release.Task;
                }
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
    public async Task CheckAsync_DownloadUrlIsAlwaysDerivedFromRepository()
    {
        // ADR 0006：下载地址由 tag 推导，不取自上游文档，因此界面拿到的地址必然属于本仓库。
        using var service = CreateService(NewerTag, out _, out var prompts);

        await service.CheckAsync(interactive: false);

        Assert.Single(prompts);
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/releases/download/v9.9.9/NMTools-v9.9.9-win-x64.zip",
            prompts[0].Manifest.DownloadUrl);
        Assert.DoesNotContain(
            "evil.example.com",
            prompts[0].Manifest.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateService CreateService(
        string? tag,
        out List<(string Message, string Title, bool IsWarning)> alerts)
    {
        return CreateService(tag, out alerts, out _);
    }

    private static UpdateService CreateService(
        string? tag,
        out List<(string Message, string Title, bool IsWarning)> alerts,
        out List<(UpdateManifest Manifest, Version CurrentVersion)> prompts,
        System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK,
        Func<Task>? asyncRespond = null)
    {
        var recordedAlerts = new List<(string Message, string Title, bool IsWarning)>();
        var recordedPrompts = new List<(UpdateManifest Manifest, Version CurrentVersion)>();
        alerts = recordedAlerts;
        prompts = recordedPrompts;
        var handler = new StubHandler(tag, statusCode, asyncRespond);

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

    /// <summary>
    /// 模拟 ADR 0006 的三个端点：latest 用 302/404 表达"有没有新稳定版"，
    /// atom 和资产列表按 URI 返回固定内容。
    /// </summary>
    private sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<Task>? _beforeRespond;
        private readonly string? _tag;
        private readonly System.Net.HttpStatusCode _statusCode;
        private readonly bool _failLatest;

        public StubHandler(
            string? tag,
            System.Net.HttpStatusCode statusCode,
            Func<Task>? beforeRespond)
        {
            _tag = tag;
            _statusCode = statusCode;
            _beforeRespond = beforeRespond;
            // 非 2xx 视为"上游不可用"，latest 也一并失败。
            _failLatest = statusCode is not System.Net.HttpStatusCode.OK || tag is null;
        }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_beforeRespond is not null)
            {
                await _beforeRespond();
            }

            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                if (_failLatest)
                {
                    return new System.Net.Http.HttpResponseMessage(
                        _statusCode == System.Net.HttpStatusCode.OK
                            ? System.Net.HttpStatusCode.NotFound
                            : _statusCode);
                }

                var redirect = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    $"https://github.com/Litt-R/NMTools/releases/tag/{_tag}");
                return redirect;
            }

            if (uri.AbsolutePath.EndsWith("/releases.atom", StringComparison.Ordinal))
            {
                return Text(NewerReleaseAtom, "application/atom+xml");
            }

            return Text(string.Empty, "text/html");
        }

        private static System.Net.Http.HttpResponseMessage Text(string body, string mediaType) =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    body,
                    System.Text.Encoding.UTF8,
                    mediaType),
            };
    }
}
