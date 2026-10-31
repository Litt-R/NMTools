using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NMTools.Core;

namespace NMTools.App.Views;

/// <summary>
/// 无焦点、鼠标穿透的轻量状态胶囊浮窗，用于在同步剪贴板期间提供即时视觉反馈，绝不抢夺 NoMachine 焦点。
/// </summary>
public partial class StatusToastWindow : Window
{
    private const int LogicalDpi = 96;
    private readonly ScreenPosition _anchorPosition;

    public StatusToastWindow(ScreenPosition anchorPosition, string message)
    {
        _anchorPosition = anchorPosition;
        InitializeComponent();
        StatusTextBlock.Text = message;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        base.OnClosed(e);
    }

    public void DismissSafely()
    {
        try
        {
            Close();
        }
        catch
        {
            // 状态胶囊属于非核心视觉提示，关闭异常不影响主流程。
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：鼠标完全穿透、永不激活、不在 Alt+Tab 出现。
        var exStyle = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLong(
            handle,
            NativeMethods.GwlExStyle,
            exStyle | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return;
        }

        var dpi = NativeMethods.GetDpiForWindow(handle);
        var scale = (dpi is >= 96 and <= 768 ? dpi : LogicalDpi) / (double)LogicalDpi;
        var gap = (int)Math.Round(12 * scale);
        var margin = (int)Math.Round(10 * scale);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_anchorPosition.X, _anchorPosition.Y));
        var workArea = screen.WorkingArea;
        var left = Math.Clamp(_anchorPosition.X - (int)Math.Round(8 * scale), workArea.Left + margin, workArea.Right - width - margin);
        var top = Math.Clamp(_anchorPosition.Y + gap, workArea.Top + margin, workArea.Bottom - height - margin);

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            left,
            top,
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    private static class NativeMethods
    {
        public const int GwlExStyle = -20;
        public const int WsExTransparent = 0x00000020;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExNoActivate = 0x08000000;
        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoActivate = 0x0010;
        public static readonly IntPtr HwndTopmost = new(-1);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr handle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
