using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NMTools.App.Infrastructure;
using NMTools.Core;

namespace NMTools.App.Views;

public partial class InputWindow : Window
{
    private const double BubbleGap = 10;
    private const double ScreenEdgeMargin = 10;
    private const double WindowVisualMargin = 8;
    private const double MaxBubbleHeight = 420;
    private const int LogicalDpi = 96;
    private static readonly TimeSpan TipRotateInterval = TimeSpan.FromSeconds(12);
    private static readonly Duration TipFadeDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration BubbleOpenFadeDuration = new(TimeSpan.FromMilliseconds(95));
    private static readonly System.Windows.Media.SolidColorBrush DefaultTipBrush =
        CreateFrozenBrush(148, 163, 184);
    private static readonly System.Windows.Media.SolidColorBrush RestoredDraftTipBrush =
        CreateFrozenBrush(37, 99, 235);

    private readonly DispatcherTimer _tipTimer;
    private readonly IReadOnlyList<string> _tips = BubbleTips.CreateRotation();
    private int _tipIndex;
    private bool _showingRestoredDraftTip;
    private readonly ScreenPosition _bubbleAnchorPosition;
    private readonly PasteShortcutBindings _pasteBindings;
    private readonly int _maxInputLength;
    private readonly bool _enableAnimations;
    private bool _isPositioning;
    private bool _hasPositioned;
    private int _calculatedLeft;
    private bool _positionUpdatePending;
    private bool _isManuallyPositioned;

    public InputWindow(
        ScreenPosition bubbleAnchorPosition,
        PasteShortcutBindings? pasteBindings = null,
        int maxInputLength = 100_000,
        string? initialText = null,
        bool enableAnimations = true)
    {
        _bubbleAnchorPosition = bubbleAnchorPosition;
        _pasteBindings = pasteBindings ?? new PasteShortcutBindings();
        _maxInputLength = Math.Clamp(maxInputLength, 1, 1_000_000);
        _enableAnimations = enableAnimations;
        InitializeComponent();
        InputTextBox.MaxLength = _maxInputLength;
        PlaceholderText.Text = BuildPlaceholderText(_pasteBindings);
        if (!string.IsNullOrEmpty(initialText))
        {
            InputTextBox.Text = initialText.Length > _maxInputLength
                ? initialText[.._maxInputLength]
                : initialText;
            _showingRestoredDraftTip = true;
            TipText.Text = "↺ 已保留上次未发送的内容（直接打字可覆盖）";
            TipText.Foreground = RestoredDraftTipBrush;
        }
        else
        {
            TipText.Text = _tips[_tipIndex];
            TipText.Foreground = DefaultTipBrush;
        }
        _tipTimer = new DispatcherTimer { Interval = TipRotateInterval };
        _tipTimer.Tick += OnTipTimerTick;
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += OnSizeChanged;
    }

    public string EnteredText => InputTextBox.Text;

    public bool WasSubmitted { get; private set; }

    public PasteShortcutAction? SelectedPasteAction { get; private set; }

    public void FocusInput(bool selectAll = false)
    {
        if (!IsVisible)
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle);
        }

        Activate();
        InputTextBox.Focus();
        Keyboard.Focus(InputTextBox);
        if (selectAll && !string.IsNullOrEmpty(InputTextBox.Text))
        {
            InputTextBox.SelectAll();
        }
        else
        {
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
        }
    }

    private void OnTipTimerTick(object? sender, EventArgs e)
    {
        if (FooterRow.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!_enableAnimations)
        {
            _showingRestoredDraftTip = false;
            TipText.Foreground = DefaultTipBrush;
            _tipIndex = (_tipIndex + 1) % _tips.Count;
            TipText.Text = _tips[_tipIndex];
            return;
        }

        var fadeOut = new DoubleAnimation(0, TipFadeDuration);
        fadeOut.Completed += (_, _) =>
        {
            _showingRestoredDraftTip = false;
            TipText.Foreground = DefaultTipBrush;
            _tipIndex = (_tipIndex + 1) % _tips.Count;
            TipText.Text = _tips[_tipIndex];
            TipText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TipFadeDuration));
        };
        TipText.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tipTimer.Start();
        UpdatePlaceholder();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                PositionBubble();
                if (_enableAnimations)
                {
                    BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, BubbleOpenFadeDuration));
                }
                else
                {
                    Opacity = 1;
                }

                ActivateInput();
            }));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _tipTimer.Stop();
        _tipTimer.Tick -= OnTipTimerTick;
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        PreviewKeyDown -= OnPreviewKeyDown;
        SizeChanged -= OnSizeChanged;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        var shortcutAction = ResolveShortcutAction(e.Key, Keyboard.Modifiers);
        if (shortcutAction is not null)
        {
            Submit(shortcutAction.Value);
            e.Handled = true;
        }
    }

    private PasteShortcutAction? ResolveShortcutAction(Key key, ModifierKeys modifiers)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            return null;
        }

        return _pasteBindings.Resolve(
            new PasteShortcut(modifiers.ToHotkeyModifiers(), (uint)virtualKey));
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_showingRestoredDraftTip)
        {
            _showingRestoredDraftTip = false;
            TipText.Foreground = DefaultTipBrush;
            TipText.Text = _tips[_tipIndex];
        }

        UpdatePlaceholder();
        if (!_isManuallyPositioned)
        {
            RequestBubblePosition();
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt)
        {
            return;
        }

        try
        {
            _isManuallyPositioned = true;
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // 窗口关闭或鼠标状态变化时，DragMove 可能无法启动；不影响输入流程。
        }
    }

    private void Submit(PasteShortcutAction action)
    {
        if (string.IsNullOrEmpty(InputTextBox.Text))
        {
            return;
        }

        SelectedPasteAction = action;
        WasSubmitted = true;
        DialogResult = true;
    }

    private void ActivateInput()
    {
        FocusInput(selectAll: !string.IsNullOrEmpty(InputTextBox.Text));
    }

    private void UpdatePlaceholder()
    {
        var hasText = !string.IsNullOrEmpty(InputTextBox.Text);
        PlaceholderText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;

        if (hasText)
        {
            var text = InputTextBox.Text;
            var lineCount = InputTextBox.LineCount;
            CharCountText.Text = lineCount > 1
                ? $"{text.Length} 字 · {lineCount} 行"
                : $"{text.Length} 字";
            FooterRow.Visibility = Visibility.Visible;
        }
        else
        {
            FooterRow.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hasPositioned && !_isManuallyPositioned && (e.WidthChanged || e.HeightChanged))
        {
            RequestBubblePosition();
        }
    }

    private void RequestBubblePosition()
    {
        if (_positionUpdatePending)
        {
            return;
        }

        _positionUpdatePending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _positionUpdatePending = false;
                PositionBubble();
            }));
    }

    private void PositionBubble()
    {
        if (!IsLoaded || _isPositioning || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _isPositioning = true;
        try
        {
            // Win32 鼠标/工作区坐标是屏幕像素，WPF 窗口位置是 DIP。
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // 应用启用 PerMonitorV2 后，Win32 屏幕坐标与当前 WPF HWND 的设备坐标一致，
            // SetWindowPos 可以直接使用屏幕像素；WPF 的尺寸仍以 DIP 表示。
            var dpi = NativeMethods.GetDpiForWindow(handle);
            var scale = (dpi is >= 96 and <= 768 ? dpi : LogicalDpi) / (double)LogicalDpi;
            var workArea = GetWorkArea(_bubbleAnchorPosition);

            MaxWidth = Math.Max(
                MinWidth,
                Math.Min(620, workArea.Width / scale - ScreenEdgeMargin * 2));
            MaxHeight = Math.Max(
                MinHeight,
                Math.Min(MaxBubbleHeight, workArea.Height / scale - ScreenEdgeMargin * 2));

            if (!NativeMethods.GetWindowRect(handle, out var windowRect))
            {
                return;
            }

            var bubbleWidth = windowRect.Right - windowRect.Left;
            var bubbleHeight = windowRect.Bottom - windowRect.Top;
            var edgeMargin = (int)Math.Round(ScreenEdgeMargin * scale);
            var visualMargin = (int)Math.Round(WindowVisualMargin * scale);
            var gap = (int)Math.Round(BubbleGap * scale);

            var workLeft = (int)Math.Round(workArea.Left);
            var workTop = (int)Math.Round(workArea.Top);
            var workRight = (int)Math.Round(workArea.Right);
            var workBottom = (int)Math.Round(workArea.Bottom);

            // 初次定位计算 X 轴位置，后续增高保持水平位置锁定，避免多显示器 DPI 舍入引起水平微小抖动
            if (!_hasPositioned)
            {
                var left = _bubbleAnchorPosition.X - visualMargin;
                _calculatedLeft = Math.Clamp(left, workLeft + edgeMargin, workRight - bubbleWidth - edgeMargin);
            }

            var belowTop = _bubbleAnchorPosition.Y + gap;
            var aboveTop = _bubbleAnchorPosition.Y - gap - bubbleHeight;
            var belowAvailable = workBottom - belowTop - edgeMargin;
            var aboveAvailable = _bubbleAnchorPosition.Y - workTop - gap - edgeMargin;
            var top = belowAvailable >= bubbleHeight || belowAvailable >= aboveAvailable
                ? belowTop
                : aboveTop;
            top = Math.Clamp(top, workTop + edgeMargin, workBottom - bubbleHeight - edgeMargin);

            NativeMethods.SetWindowPos(
                handle,
                IntPtr.Zero,
                _calculatedLeft,
                top,
                0,
                0,
                NativeMethods.SetWindowPosNoSize
                    | NativeMethods.SetWindowPosNoActivate
                    | NativeMethods.SetWindowPosNoZOrder);
            _hasPositioned = true;
        }
        finally
        {
            _isPositioning = false;
        }
    }

    private static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string BuildPlaceholderText(PasteShortcutBindings bindings)
    {
        var primary = bindings.Enter.Shortcut is not null && bindings.Enter.Method != PasteMethod.None
            ? $"{HotkeyFormatter.Format(bindings.Enter.Shortcut)} 发送"
            : "快捷键发送";
        return $"输入文字… {primary} · Shift+Enter换行 · Esc取消 · Alt+拖拽";
    }

    private static Rect GetWorkArea(ScreenPosition position)
    {
        var point = new System.Drawing.Point(position.X, position.Y);
        var screen = System.Windows.Forms.Screen.FromPoint(point);
        return new Rect(
            screen.WorkingArea.Left,
            screen.WorkingArea.Top,
            screen.WorkingArea.Width,
            screen.WorkingArea.Height);
    }

    private static class NativeMethods
    {
        public const uint SetWindowPosNoSize = 0x0001;
        public const uint SetWindowPosNoMove = 0x0002;
        public const uint SetWindowPosNoZOrder = 0x0004;
        public const uint SetWindowPosNoActivate = 0x0010;

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr handle);

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
