using System.Net.Http;
using System.Windows;
using NMTools.App.Infrastructure;
using NMTools.App.Views;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 把 Core 的版本检查接到 Windows 上：发起检查、弹出提示、打开浏览器。
///
/// 所有对外方法都保证不抛异常——更新检查是可选功能，任何时候失败都不能影响
/// 主窗口、托盘驻留和全局热键。
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly GitHubReleaseFeed _feed;
    private readonly UpdateChecker _checker;
    private readonly IDiagnosticLog? _log;
    private readonly Func<Window?> _ownerAccessor;
    private readonly Action<string, string, bool, Window?> _showAlert;
    private readonly Func<UpdateManifest, Version, Window?, bool>? _promptForUpdate;
    private int _checking;

    /// <param name="promptForUpdate">
    /// 展示更新提示并返回用户是否选择「下载并更新」。为 null 时使用真实对话框；
    /// 测试通过注入它避免在非 UI 线程上弹出窗口。
    /// </param>
    public UpdateService(
        Version currentVersion,
        Func<Window?> ownerAccessor,
        IDiagnosticLog? log = null,
        Action<string, string, bool, Window?>? showAlert = null,
        HttpMessageHandler? handler = null,
        Func<UpdateManifest, Version, Window?, bool>? promptForUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(ownerAccessor);

        _log = log;
        _ownerAccessor = ownerAccessor;
        _promptForUpdate = promptForUpdate;
        _showAlert = showAlert ?? ((message, title, isWarning, owner) =>
            ConfirmDialog.ShowAlert(owner, message, title, isWarning));
        _feed = new GitHubReleaseFeed(log, handler);
        _checker = new UpdateChecker(_feed, currentVersion);
    }

    /// <summary>
    /// 当前是否正在检查，用于禁用按钮、避免重复请求和重复弹窗。
    /// </summary>
    public bool IsChecking => Volatile.Read(ref _checking) != 0;

    /// <summary>
    /// 执行一次检查。
    /// </summary>
    /// <param name="interactive">
    /// true 表示用户手动点击「检查更新」：失败和"已是最新"都要给出提示。
    /// false 表示启动时的自动检查：只在发现新版本时弹窗，其余情况完全静默。
    /// </param>
    /// <param name="owner">
    /// 弹窗的 Owner。从设置窗口发起检查时必须传设置窗口本身——它是模态对话框，
    /// 以主窗口为 Owner 的弹窗会被压在它后面，用户看不到。
    /// </param>
    public async Task CheckAsync(
        bool interactive,
        CancellationToken cancellationToken = default,
        Window? owner = null)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var startTimestamp = Environment.TickCount64;
            var result = await _checker.CheckAsync(cancellationToken).ConfigureAwait(true);
            _log?.Timing("update.check.finished", Environment.TickCount64 - startTimestamp);

            // 诊断日志只记结论和分类，不记响应正文、更新说明或任何 URL 内容。
            _log?.Info(
                "update.check.result",
                $"status={result.Status} reason={result.FailureReason}"
                + (result.HttpStatusCode is int code ? $" http={code}" : string.Empty)
                + (result.Manifest is not null ? $" latest={result.Manifest.Version}" : string.Empty));

            var effectiveOwner = owner ?? _ownerAccessor();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    PromptForUpdate(result.Manifest!, effectiveOwner);
                    break;
                case UpdateCheckStatus.UpToDate:
                    if (interactive)
                    {
                        _showAlert(
                            $"当前已是最新版本（{UpdateDisplay.FormatVersion(_checker.CurrentVersion)}）。",
                            "NMTools - 检查更新",
                            false,
                            effectiveOwner);
                    }

                    break;
                case UpdateCheckStatus.Failed:
                default:
                    // 启动时的自动检查完全静默；手动检查也只给一句可操作提示。
                    if (interactive && result.FailureReason != UpdateCheckFailureReason.Canceled)
                    {
                        _showAlert(UpdateCheckResult.FailureMessage, "NMTools - 检查更新", false, effectiveOwner);
                    }

                    break;
            }
        }
        catch (Exception exception)
        {
            _log?.Error("update.check.unexpected", "update check failed unexpectedly", exception);
            if (interactive)
            {
                _showAlert(UpdateCheckResult.FailureMessage, "NMTools - 检查更新", false, owner);
            }
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void PromptForUpdate(UpdateManifest manifest, Window? owner)
    {
        bool openRequested;
        string releasePageUrl;
        if (_promptForUpdate is not null)
        {
            openRequested = _promptForUpdate(manifest, _checker.CurrentVersion, owner);
            releasePageUrl = manifest.ReleasePageUrl;
        }
        else
        {
            var dialog = UpdateAvailableDialog.Show(owner, manifest, _checker.CurrentVersion);
            openRequested = dialog.OpenReleasePageRequested;
            releasePageUrl = dialog.ReleasePageUrl;
        }

        if (openRequested)
        {
            OpenReleasePage(releasePageUrl, owner);
        }
    }

    /// <summary>
    /// 在默认浏览器中打开 Release 页面。打开前再次校验链接属于本仓库，
    /// 不把响应内容里的任意地址交给 ShellExecute。
    /// </summary>
    private void OpenReleasePage(string url, Window? owner)
    {
        if (!UpdateSource.IsTrustedReleaseUrl(url))
        {
            _log?.Warn("update.openReleasePage.rejected", "release page url is not a trusted repository url");
            _showAlert(UpdateDisplay.OpenReleasePageFailedMessage, "NMTools - 下载更新", true, owner);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            _log?.Info("update.openReleasePage", "user opened the release page");
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log?.Warn("update.openReleasePage.failed", $"ex={exception.GetType().Name}");
            _showAlert(UpdateDisplay.OpenReleasePageFailedMessage, "NMTools - 下载更新", true, owner);
        }
    }

    public void Dispose()
    {
        _feed.Dispose();
    }
}
