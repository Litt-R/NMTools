using System.Drawing;
using Forms = System.Windows.Forms;
using NMTools.App.Views;
using NMTools.Core;

namespace NMTools.App.Infrastructure;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly bool _ownsTrayIcon;
    private readonly Action _showWindow;
    private readonly Action _showSettings;
    private readonly Action _openLogs;
    private readonly Action _exitApplication;
    private TrayMenuWindow? _menuWindow;
    private bool _disposed;

    public TrayIconService(
        Action showWindow,
        Action showSettings,
        Action openLogs,
        Action exitApplication)
    {
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _showSettings = showSettings ?? throw new ArgumentNullException(nameof(showSettings));
        _openLogs = openLogs ?? throw new ArgumentNullException(nameof(openLogs));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));

        (_trayIcon, _ownsTrayIcon) = CreateIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "NMTools 输入桥接",
            Visible = true,
        };
        _notifyIcon.DoubleClick += OnDoubleClick;
        _notifyIcon.MouseUp += OnMouseUp;
    }

    public void UpdateToolTip(InputBridgeOptions options)
    {
        if (_disposed || options is null)
        {
            return;
        }

        // NotifyIcon.Text 限制最长 63 个字符。
        var trigger = HotkeyFormatter.Format(options);
        var retry = options.HasRetryHotkey ? HotkeyFormatter.FormatRetry(options) : "关";
        var text = $"NMTools | 气泡:{trigger} | 重试:{retry}";
        if (text.Length > 63)
        {
            text = text[..63];
        }

        _notifyIcon.Text = text;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.DoubleClick -= OnDoubleClick;
        _notifyIcon.MouseUp -= OnMouseUp;
        _menuWindow?.CloseSafely();
        _menuWindow = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_ownsTrayIcon)
        {
            _trayIcon.Dispose();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        _showWindow();
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right || _disposed)
        {
            return;
        }

        ShowMenu(Forms.Cursor.Position);
    }

    private void ShowMenu(Point screenPosition)
    {
        _menuWindow?.CloseSafely();

        var menuWindow = new TrayMenuWindow(
            screenPosition,
            _showWindow,
            _showSettings,
            _openLogs,
            _exitApplication);
        _menuWindow = menuWindow;
        menuWindow.Closed += OnMenuClosed;
        menuWindow.Show();
        menuWindow.Activate();
    }

    private void OnMenuClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_menuWindow, sender))
        {
            _menuWindow = null;
        }
    }

    private static (Icon Icon, bool OwnsIcon) CreateIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("/Assets/Branding/NMTools.ico", UriKind.Relative));
        if (resource is not null)
        {
            using (resource.Stream)
            using (var source = new Icon(resource.Stream))
            {
                return (new Icon(source, new Size(16, 16)), true);
            }
        }

        return (SystemIcons.Application, false);
    }
}
