using System.Windows;
using NMTools.Core.Updates;

namespace NMTools.App.Views;

/// <summary>
/// 下载与更新进度窗口。
///
/// 三个阶段：
/// 1. 下载中 —— 显示字节进度，只提供「取消」；
/// 2. 下载完成后 —— 提供「立即重启更新」和「下次启动时更新」；
/// 3. 失败 —— 显示原因，只能关闭。
/// </summary>
public partial class UpdateProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _completed;

    public UpdateProgressDialog(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        InitializeComponent();
        HeaderText.Text = $"正在下载 {UpdateDisplay.FormatVersion(manifest.Version)}…";
        Progress.Maximum = manifest.DownloadSizeBytes ?? 100;
        if (manifest.DownloadSizeBytes is null)
        {
            Progress.IsIndeterminate = true;
        }
    }

    public enum InstallChoice
    {
        None,
        RestartNow,
        NextStartup,
        Cancelled,
        Failed,
    }

    public InstallChoice Choice { get; private set; } = InstallChoice.None;

    public CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>
    /// 构造对话框并设置 Owner。调用方应使用 <c>ShowDialog()</c> 模态显示，
    /// 因为下载期间不能让用户继续操作设置窗口。
    /// </summary>
    public static UpdateProgressDialog Create(Window? owner, UpdateManifest manifest)
    {
        var dialog = new UpdateProgressDialog(manifest);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        return dialog;
    }

    public void ReportProgress(long bytesDownloaded, long? totalBytes)
    {
        if (_completed || !Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportProgress(bytesDownloaded, totalBytes));
            return;
        }

        if (totalBytes is long total && total > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Maximum = total;
            Progress.Value = Math.Min(bytesDownloaded, total);
            DetailText.Text = $"{UpdateDisplay.FormatSize(bytesDownloaded)} / {UpdateDisplay.FormatSize(total)}";
        }
        else
        {
            DetailText.Text = UpdateDisplay.FormatSize(bytesDownloaded) ?? string.Empty;
        }
    }

    public void ReportStage(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportStage(message));
            return;
        }

        StatusText.Text = message;
    }

    /// <summary>
    /// 下载并校验完成，切换到"是否立即更新"状态。
    /// </summary>
    public void ShowReady()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowReady);
            return;
        }

        _completed = true;
        Progress.IsIndeterminate = false;
        Progress.Value = Progress.Maximum;
        HeaderText.Text = "更新已就绪";
        StatusText.Text = "已完成下载和完整性校验。";
        DetailText.Text = string.Empty;
        CancelPanel.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 失败：把窗口切换成失败状态，等用户看清楚后再自己关闭。
    /// 不自动关窗，否则用户只会看到一个闪过的窗口，不知道发生了什么。
    /// </summary>
    public void ShowFailure(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowFailure(message));
            return;
        }

        _completed = true;
        Choice = InstallChoice.Failed;
        HeaderText.Text = "更新失败";
        StatusText.Text = message;
        DetailText.Text = string.Empty;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;

        ActionPanel.Visibility = Visibility.Collapsed;
        CancelPanel.Visibility = Visibility.Visible;
        CancelButton.Content = "知道了";
        CancelButton.IsDefault = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 窗口被直接关掉（例如用户点右上角关闭或程序退出）时，取消正在进行的下载。
        if (!_completed)
        {
            _cancellation.Cancel();
        }

        base.OnClosed(e);
    }

    private void OnRestartNowClick(object sender, RoutedEventArgs e)
    {
        Choice = InstallChoice.RestartNow;
        _completed = true;
        DialogResult = true;
    }

    private void OnInstallLaterClick(object sender, RoutedEventArgs e)
    {
        Choice = InstallChoice.NextStartup;
        _completed = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            DialogResult = false;
            return;
        }

        Choice = InstallChoice.Cancelled;
        _cancellation.Cancel();
        DialogResult = false;
    }
}
