using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace NMTools.App.Views;

public partial class TrayMenuWindow : Window
{
    private readonly Action _openAction;
    private readonly Action _settingsAction;
    private readonly Action _logsAction;
    private readonly Action _exitAction;
    private bool _isClosing;

    public TrayMenuWindow(
        Drawing.Point screenPosition,
        Action openAction,
        Action settingsAction,
        Action logsAction,
        Action exitAction)
    {
        _openAction = openAction ?? throw new ArgumentNullException(nameof(openAction));
        _settingsAction = settingsAction ?? throw new ArgumentNullException(nameof(settingsAction));
        _logsAction = logsAction ?? throw new ArgumentNullException(nameof(logsAction));
        _exitAction = exitAction ?? throw new ArgumentNullException(nameof(exitAction));

        InitializeComponent();
        Loaded += (_, _) => PositionNearTray(screenPosition);
        // 无论谁触发关闭（点菜单项、失去焦点、托盘服务关掉旧菜单、程序退出），窗口一开始关闭就记下来。
        // 之前只有 CloseMenu 会置位，托盘服务直接调用 Close() 时标志仍为 false，
        // 关闭过程中的失去激活又会再调一次 Close()，WPF 因此抛出“窗口关闭期间无法调用 Close”。
        Closing += (_, _) => _isClosing = true;
    }

    /// <summary>
    /// 供托盘服务在替换或销毁菜单时调用。已经在关闭中时什么也不做，可以重复调用。
    /// </summary>
    public void CloseSafely()
    {
        CloseMenu();
    }

    private void PositionNearTray(Drawing.Point screenPosition)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var workArea = Forms.Screen.GetWorkingArea(screenPosition);
        var widthDip = ActualWidth > 0 ? ActualWidth : Width;
        var heightDip = ActualHeight > 0 ? ActualHeight : Height;
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        var width = (int)Math.Round(widthDip * scaleX);
        var height = (int)Math.Round(heightDip * scaleY);
        var left = Math.Clamp(
            screenPosition.X - width,
            workArea.Left,
            workArea.Right - width);
        var top = Math.Clamp(
            screenPosition.Y - height,
            workArea.Top,
            workArea.Bottom - height);

        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            left,
            top,
            width,
            height,
            NativeMethods.SetWindowPosNoActivate
                | NativeMethods.SetWindowPosNoZOrder);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        RunMenuAction(_openAction);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        RunMenuAction(_settingsAction);
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        RunMenuAction(_logsAction);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        RunMenuAction(_exitAction);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        CloseMenu();
    }

    private void RunMenuAction(Action action)
    {
        if (_isClosing)
        {
            return;
        }

        CloseMenu();
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Send,
            action);
    }

    private void CloseMenu()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // 窗口已经在关闭过程中（例如被外部同时关闭），目的已经达到，无需处理。
        }
    }

    private void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseMenu();
            e.Handled = true;
        }
    }

    private static class NativeMethods
    {
        public const uint SetWindowPosNoZOrder = 0x0004;
        public const uint SetWindowPosNoActivate = 0x0010;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetWindowPos(
            IntPtr handle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
