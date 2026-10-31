using System.Windows;
using NMTools.Core.Updates;

namespace NMTools.App.Views;

/// <summary>
/// 发现新版本时的提示窗口。展示版本号、更新说明和下载体积，
/// 两个按钮分别是「下载并更新」（进入下载与安装流程）和「以后再说」（关闭，不记住任何状态）。
/// </summary>
public partial class UpdateAvailableDialog : Window
{
    public UpdateAvailableDialog(UpdateManifest manifest, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentVersion);

        InitializeComponent();
        HeaderText.Text = UpdateDisplay.FormatHeadline(manifest.Version, currentVersion);

        // 说明和体积都来自网络响应，这里只做纯文本展示，不解析 Markdown、不加载任何外部资源。
        NotesText.Text = UpdateDisplay.FormatNotes(manifest.Notes);
        var size = UpdateDisplay.FormatSize(manifest.DownloadSizeBytes);
        if (size is null)
        {
            SizeText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SizeText.Text = $"下载包体积约 {size}";
            SizeText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 用户选择了「下载并更新」。调用方据此进入下载与安装流程。
    /// </summary>
    public bool UpdateRequested { get; private set; }

    /// <summary>
    /// 以当前可见窗口为 Owner 弹出提示；主窗口隐藏时（托盘启动）改为独立显示。
    /// </summary>
    public static UpdateAvailableDialog Show(Window? owner, UpdateManifest manifest, Version currentVersion)
    {
        var dialog = new UpdateAvailableDialog(manifest, currentVersion);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            // 没有可见 Owner 时对话框必须自己置顶，否则可能被其他窗口盖住而让用户以为程序卡住。
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        dialog.ShowDialog();
        return dialog;
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        // 「以后再说」不写任何状态：下次发现同一版本仍会提示，这是刻意的
        // ——不引入"已跳过版本"这类需要持久化和清理的状态。
        UpdateRequested = false;
        DialogResult = false;
        e.Handled = true;
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        UpdateRequested = true;
        DialogResult = true;
        e.Handled = true;
    }
}
