using System.IO;
using System.Net.Http;
using System.Windows;
using NMTools.App.Infrastructure;
using NMTools.App.Views;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 把 Core 的版本检查接到 Windows 上：发起检查、提示更新、驱动下载与安装。
///
/// 完整流程（用户点击「下载并更新」后）：
/// <code>
/// 检查更新 -> 有新版本 -> UpdateAvailableDialog
///   -> 用户确认下载 -> UpdateProgressDialog（下载 + 校验 + 解压）
///      -> 立即重启更新：启动 NMTools.Updater.exe，主程序退出
///      -> 下次启动时更新：写下待办标记，本次不重启
/// </code>
///
/// 所有对外方法都保证不抛异常——更新是可选功能，任何失败都不能影响
/// 主窗口、托盘驻留和全局热键。
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly GitHubReleaseFeed _feed;
    private readonly UpdateChecker _checker;
    private readonly UpdateInstaller _installer;
    private readonly IDiagnosticLog? _log;
    private readonly Func<Window?> _ownerAccessor;
    private readonly Action<string, string, bool, Window?> _showAlert;
    private readonly Func<UpdateManifest, Version, Window?, bool>? _promptForUpdate;
    private readonly Func<UpdateManifest, Window?, InstallDecision>? _runInstallFlow;
    private readonly string _appDirectory;
    private int _checking;
    private int _installing;

    /// <param name="promptForUpdate">
    /// 展示"发现新版本"对话框并返回用户是否要下载。为 null 时使用真实对话框；
    /// 测试通过注入它避免在非 UI 线程上弹窗。
    /// </param>
    /// <param name="runInstallFlow">
    /// 执行下载与安装交互并返回用户的选择。为 null 时使用真实的进度窗口；
    /// 测试通过注入它避免真实下载和弹窗。
    /// </param>
    public UpdateService(
        Version currentVersion,
        Func<Window?> ownerAccessor,
        IDiagnosticLog? log = null,
        Action<string, string, bool, Window?>? showAlert = null,
        HttpMessageHandler? handler = null,
        Func<UpdateManifest, Version, Window?, bool>? promptForUpdate = null,
        Func<UpdateManifest, Window?, InstallDecision>? runInstallFlow = null,
        string? appDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(ownerAccessor);

        _log = log;
        _ownerAccessor = ownerAccessor;
        _promptForUpdate = promptForUpdate;
        _runInstallFlow = runInstallFlow;
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        _showAlert = showAlert ?? ((message, title, isWarning, owner) =>
            ConfirmDialog.ShowAlert(owner, message, title, isWarning));
        _feed = new GitHubReleaseFeed(log, handler);
        _checker = new UpdateChecker(_feed, currentVersion);
        _installer = new UpdateInstaller(log, handler);
    }

    /// <summary>当前是否正在检查或安装，用于禁用按钮、避免重复请求和重复弹窗。</summary>
    public bool IsBusy =>
        Volatile.Read(ref _checking) != 0 || Volatile.Read(ref _installing) != 0;

    /// <summary>应用准备退出以便更新器替换文件时触发。参数为更新器进程 PID。</summary>
    public event EventHandler<int>? UpdateReadyToApply;

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
        bool userWantsUpdate;
        if (_promptForUpdate is not null)
        {
            userWantsUpdate = _promptForUpdate(manifest, _checker.CurrentVersion, owner);
        }
        else
        {
            var dialog = UpdateAvailableDialog.Show(owner, manifest, _checker.CurrentVersion);
            userWantsUpdate = dialog.OpenReleasePageRequested;
        }

        if (!userWantsUpdate)
        {
            return;
        }

        if (!manifest.SupportsAutomaticUpdate)
        {
            // 该版本没有提供校验文件，无法安全地自动下载。退回打开浏览器，
            // 而不是在没有完整性校验的情况下执行下载的字节。
            _log?.Warn("update.install.notSupported", "release has no checksum asset");
            OpenReleasePage(manifest.ReleasePageUrl, owner);
            return;
        }

        BeginInstall(manifest, owner);
    }

    /// <summary>
    /// 执行安装流程。下载在后台线程进行，界面更新回到 UI 线程。
    /// </summary>
    private void BeginInstall(UpdateManifest manifest, Window? owner)
    {
        if (_runInstallFlow is not null)
        {
            // 测试注入：不启动真实下载和窗口。
            _ = _runInstallFlow(manifest, owner);
            return;
        }

        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            return;
        }

        var dialog = UpdateProgressDialog.Create(owner, manifest);
        // ShowDialog 阻塞 UI 线程，因此下载在单独的 async 流程里跑，
        // 并通过 dialog 的 Dispatcher 回写进度。
        var installTask = RunInstallAsync(manifest, dialog);
        dialog.ShowDialog();

        // 窗口关闭后才有确定的选择。下载若已开始，这里只是等待它收尾。
        var choice = dialog.Choice switch
        {
            UpdateProgressDialog.InstallChoice.RestartNow => InstallChoice.RestartNow,
            UpdateProgressDialog.InstallChoice.NextStartup => InstallChoice.NextStartup,
            UpdateProgressDialog.InstallChoice.Cancelled => InstallChoice.Cancelled,
            UpdateProgressDialog.InstallChoice.Failed => InstallChoice.Failed,
            _ => InstallChoice.None,
        };

        if (choice is InstallChoice.RestartNow or InstallChoice.NextStartup)
        {
            ApplyInstallChoice(choice, manifest);
        }

        // 取消时下载器已通过 dialog.CancellationToken 收到信号，不需要额外处理。
        // 未观察的异常在这里吞掉：更新失败不能冒泡到 UI 线程。
        _ = installTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task RunInstallAsync(UpdateManifest manifest, UpdateProgressDialog dialog)
    {
        try
        {
            dialog.ReportStage("正在校验更新包完整性…");
            var progress = new Progress<long>(bytes =>
                dialog.ReportProgress(bytes, manifest.DownloadSizeBytes));

            var prepared = await _installer
                .PrepareAsync(manifest, _appDirectory, progress, dialog.CancellationToken)
                .ConfigureAwait(true);

            if (!prepared.IsSuccess)
            {
                dialog.ShowFailure(DescribePrepareFailure(prepared.FailureReason));
                return;
            }

            dialog.ShowReady();
        }
        catch (Exception exception)
        {
            _log?.Error("update.install.unexpected", "install flow failed", exception);
            dialog.ShowFailure("更新失败，可稍后重试。当前版本可以继续使用。");
        }
        finally
        {
            Volatile.Write(ref _installing, 0);
        }
    }

    /// <summary>
    /// 处理用户在进度窗口里做出的选择。由窗口关闭后调用。
    /// </summary>
    /// <param name="choice">用户选择。</param>
    /// <param name="manifest">当前更新清单。</param>
    public void ApplyInstallChoice(InstallChoice choice, UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        switch (choice)
        {
            case InstallChoice.RestartNow:
                StartUpdaterAndExit(manifest);
                break;
            case InstallChoice.NextStartup:
                ScheduleForNextStartup(manifest);
                break;
            default:
                // 取消或失败：暂存目录留给下次，不清也不应用。
                break;
        }
    }

    private void StartUpdaterAndExit(UpdateManifest manifest)
    {
        var contentDirectory = Path.Combine(
            PortableLayout.ResolveStagingDirectory(_appDirectory),
            "content");

        var updaterPid = _installer.LaunchUpdater(_appDirectory, contentDirectory);
        if (updaterPid is null)
        {
            _showAlert(
                "无法启动更新程序，请手动下载新版本替换。当前版本可以继续使用。",
                "NMTools - 更新失败",
                true,
                _ownerAccessor());
            return;
        }

        _log?.Info("update.apply.restartNow", $"version={manifest.Version}");
        UpdateReadyToApply?.Invoke(this, updaterPid.Value);
    }

    private void ScheduleForNextStartup(UpdateManifest manifest)
    {
        var contentDirectory = Path.Combine(
            PortableLayout.ResolveStagingDirectory(_appDirectory),
            "content");

        var saved = _installer.ScheduleForNextStartup(_appDirectory, contentDirectory, manifest.Version);
        if (!saved)
        {
            _showAlert(
                "无法记录更新，请稍后在设置中重新检查更新。当前版本可以继续使用。",
                "NMTools - 更新失败",
                true,
                _ownerAccessor());
            return;
        }

        _log?.Info("update.apply.scheduled", $"version={manifest.Version}");
        _showAlert(
            $"更新已准备就绪，下次启动 NMTools 时会自动应用 {UpdateDisplay.FormatVersion(manifest.Version)}。",
            "NMTools - 更新已排期",
            false,
            _ownerAccessor());
    }

    /// <summary>
    /// 在默认浏览器中打开 Release 页面。仅在没有校验文件、无法自动更新时使用。
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

    private static string DescribePrepareFailure(PrepareFailureReason reason) => reason switch
    {
        PrepareFailureReason.ChecksumMismatch =>
            "下载的更新包校验失败，文件可能不完整或被改动，已删除。请稍后重试。",
        PrepareFailureReason.ChecksumUnavailable =>
            "无法获取更新包的校验信息，已取消更新。请稍后重试。",
        PrepareFailureReason.SizeMismatch =>
            "下载的更新包大小不正确，已删除。请稍后重试。",
        PrepareFailureReason.InvalidArchive =>
            "更新包格式不正确，无法解压。请稍后重试或从发布页手动下载。",
        PrepareFailureReason.UnsafeArchive =>
            "更新包包含不安全的文件路径，已拒绝解压。请联系维护者。",
        PrepareFailureReason.StorageFailure =>
            "无法写入程序目录。请检查磁盘空间和目录写权限。",
        PrepareFailureReason.Canceled => "更新已取消。当前版本可以继续使用。",
        _ => "下载更新失败，可稍后重试。当前版本可以继续使用。",
    };

    public void Dispose()
    {
        _feed.Dispose();
        _installer.Dispose();
    }
}

/// <summary>
/// 用户在进度窗口里做出的选择。
/// </summary>
public enum InstallChoice
{
    None,
    RestartNow,
    NextStartup,
    Cancelled,
    Failed,
}

/// <summary>
/// 安装流程的结果，供测试注入使用。
/// </summary>
public sealed record InstallDecision(InstallChoice Choice, UpdateManifest Manifest);
