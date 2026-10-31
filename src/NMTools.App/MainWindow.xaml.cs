using System.Windows;
using System.Windows.Interop;
using NMTools.App.Infrastructure;
using NMTools.App.Interop;
using NMTools.App.Services;
using NMTools.App.Views;
using NMTools.Core;

namespace NMTools.App;

public partial class MainWindow : Window
{
    private InputBridgeOptions _options;
    private readonly IDiagnosticLog? _log;
    private readonly AppSettingsStore _appSettingsStore = new();
    private readonly NoMachineWindowService _targetWindowService = new();
    private readonly WpfClipboardService _clipboardService;
    private readonly SendInputTextInjectionService _textInjectionService;
    private readonly WindowSettingsStore _windowSettingsStore = new();
    private readonly UpdateService _updateService;
    private GlobalHotkeyService? _hotkeyService;
    private InputWindow? _activeInputWindow;
    private TargetWindow? _lastSubmittedTarget;
    private string? _lastSubmittedText;
    private PasteShortcutAction? _lastSubmittedPasteAction;
    private string? _unsentDraftText;
    private int _inputFlowActive;
    private int _retryQueued;
    private bool _settingsOpen;
    private bool _allowClose;
    private bool _resourcesDisposed;
    private readonly object _windowSettingsSaveLock = new();
    private Task _pendingWindowSettingsSave = Task.CompletedTask;

    public event EventHandler<InputBridgeOptions>? OptionsChanged;

    public InputBridgeOptions CurrentOptions => _options;

    /// <summary>
    /// 当前程序集版本。主窗口徽标、设置窗口和更新检查都从这里取，保证三处显示一致。
    /// </summary>
    public static Version AssemblyVersion => typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public MainWindow(IDiagnosticLog? log = null)
    {
        _log = log;
        _options = _appSettingsStore.Load() ?? new InputBridgeOptions();
        _clipboardService = new WpfClipboardService(_log);
        _textInjectionService = new SendInputTextInjectionService(_targetWindowService, _log);
        _updateService = new UpdateService(AssemblyVersion, () => this, _log);
        InitializeComponent();
        VersionText.Text = VersionLabel.Format(AssemblyVersion);
        ApplyInitialPlacement();
        UpdateShortcutDescription();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        ShowActivated = true;
        if (!IsVisible)
        {
            // 仅 EnsureHandle 创建过句柄的窗口必须经由 Show() 才能被 Activate。
            Show();
        }

        var wasMinimized = WindowState == WindowState.Minimized;
        if (wasMinimized)
        {
            WindowState = WindowState.Normal;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            if (wasMinimized)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
            }

            NativeMethods.SetForegroundWindow(handle);
        }

        Activate();
    }

    public void PrepareHidden()
    {
        // 只创建窗口句柄、不显示窗口，使 SourceInitialized 里的全局热键注册照常执行。
        _ = new WindowInteropHelper(this).EnsureHandle();
    }

    public void ShowSettings()
    {
        if (_settingsOpen)
        {
            return;
        }

        _settingsOpen = true;
        try
        {
            // UI 自动化模式不注入更新服务：设置窗口里的「检查更新」按钮会被禁用，
            // 测试不会发起真实网络请求，也不会因为 GitHub 速率限制而随机失败。
            var settingsWindow = new SettingsWindow(_options, IsUiAutomation ? null : _updateService);
            // 启动时主窗口保持隐藏，隐藏窗口不能作为 WPF 对话框 Owner。
            // 设置入口来自托盘，因此主窗口隐藏时让设置窗口独立显示。
            if (IsVisible)
            {
                settingsWindow.Owner = this;
            }

            settingsWindow.ShowDialog();
            if (settingsWindow.DialogResult != true || settingsWindow.UpdatedOptions is null)
            {
                return;
            }

            ApplyOptions(settingsWindow.UpdatedOptions);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"无法打开设置窗口：{exception.Message}",
                "NMTools - 设置失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    public void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        _allowClose = true;
        WaitForPendingWindowSettingsSave();
        SaveWindowSettings();
        DisposeHotkey();
        _updateService.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeResources();
        SourceInitialized -= OnSourceInitialized;
        Closing -= OnClosing;
        Loaded -= OnLoaded;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateShortcutDescription();
    }

    /// <summary>
    /// 启动时自动检查更新。由 <see cref="App"/> 在启动流程末尾调用一次，覆盖两条启动路径：
    /// 手动双击（窗口显示）和开机自启动（窗口隐藏、只驻留托盘）。
    ///
    /// 不能挂在 <c>Loaded</c> 上：托盘启动只创建窗口句柄、从不显示窗口，该事件不会触发。
    /// 本方法不改变窗口可见性，也不 await、不阻塞；检查失败完全静默，
    /// 成功且确实有新版本时才会弹窗。
    /// </summary>
    public void StartStartupUpdateCheckIfEnabled()
    {
        if (!_options.CheckUpdatesOnStartup || IsUiAutomation)
        {
            return;
        }

        // 只查一次，不轮询。失败由 UpdateService 内部吞掉，这里不需要额外处理。
        _ = CheckForUpdatesAtStartupAsync();
    }

    private async Task CheckForUpdatesAtStartupAsync()
    {
        try
        {
            // 排到 ApplicationIdle：启动消息（显示窗口、注册热键、托盘图标）先处理完，
            // 网络再慢也不会拖慢启动过程本身。
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await _updateService.CheckAsync(interactive: false);
        }
        catch
        {
            // 自动检查绝不能影响主程序：任何异常都在这里终止，不冒泡到 UI 线程。
        }
    }

    /// <summary>
    /// UI 自动化模式下不发起任何真实网络请求，避免测试依赖网络或被速率限制影响。
    /// </summary>
    private bool IsUiAutomation =>
        (System.Windows.Application.Current as App)?.IsUiAutomationMode == true;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            QueueWindowSettingsSave();
            return;
        }

        SaveWindowSettings();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if ((System.Windows.Application.Current as App)?.IsUiAutomationMode == true)
        {
            return;
        }

        // EnsureHandle 期间 PresentationSource.FromVisual 尚不可用，改用句柄取 HwndSource。
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)
            ?? throw new InvalidOperationException("无法取得 NMTools 主窗口句柄。");

        _hotkeyService = new GlobalHotkeyService(source);
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.RetryHotkeyPressed += OnRetryHotkeyPressed;

        try
        {
            _hotkeyService.Register(_options);
        }
        catch (InvalidOperationException exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "NMTools - 快捷键注册失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ApplyOptions(InputBridgeOptions updatedOptions)
    {
        if (_hotkeyService is null)
        {
            _options = updatedOptions;
            _appSettingsStore.Save(_options);
            OptionsChanged?.Invoke(this, _options);
            return;
        }

        try
        {
            _hotkeyService.Unregister();
            _hotkeyService.Register(updatedOptions);
            _options = updatedOptions;
            if (!_appSettingsStore.Save(_options))
            {
                ConfirmDialog.ShowAlert(
                    this,
                    "快捷键已在本次运行中生效，但设置文件无法写入。请检查程序目录下 data 文件夹的写入权限。",
                    "NMTools - 设置未持久化",
                    isWarning: true);
            }

            UpdateShortcutDescription();
            OptionsChanged?.Invoke(this, _options);
        }
        catch (InvalidOperationException exception)
        {
            try
            {
                _hotkeyService.Register(_options);
            }
            catch (InvalidOperationException)
            {
                // 原快捷键恢复失败时仍保留内存中的旧配置，并提示用户重新启动应用。
            }

            ConfirmDialog.ShowAlert(
                this,
                exception.Message,
                "NMTools - 快捷键保存失败",
                isWarning: true);
        }
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left
            || e.ButtonState != System.Windows.Input.MouseButtonState.Pressed
            || FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        e.Handled = true;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(
            handle,
            NativeMethods.WmNcLButtonDown,
            new IntPtr(NativeMethods.HtCaption),
            IntPtr.Zero);
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Hide();
        QueueWindowSettingsSave();
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T result)
            {
                return result;
            }

            if (source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            {
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }
            else
            {
                source = LogicalTreeHelper.GetParent(source);
            }
        }

        return null;
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowSettings();
    }

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
    {
        Hide();
        QueueWindowSettingsSave();
    }

    private void UpdateShortcutDescription()
    {
        RenderKeyCaps(KeyCapContainer, HotkeyFormatter.FormatParts(_options));
        var retryParts = HotkeyFormatter.FormatRetryParts(_options);
        if (retryParts.Count > 0)
        {
            RenderKeyCaps(RetryKeyCapContainer, retryParts);
        }
        else
        {
            RetryKeyCapContainer.Children.Clear();
            RetryKeyCapContainer.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "未设置（可在设置中配置）",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var delayMs = (int)Math.Round(_options.PostActivationClipboardSettleDelay.TotalMilliseconds);
        SyncDelayBadgeText.Text = $"同步等待 {delayMs} ms";

        var retryHint = _options.HasRetryHotkey
            ? $"若遇 NoMachine 剪贴板同步延迟未粘上，按 {HotkeyFormatter.FormatRetry(_options)} 直接补发一次。"
            : "可在设置中开启重试粘贴快捷键与调整同步等待延迟。";
        ShortcutDescription.Text =
            $"在 NoMachine 远程窗口中点击目标输入框，按下 {HotkeyFormatter.Format(_options)} 唤起输入气泡。\n{retryHint}";
    }

    private void RenderKeyCaps(System.Windows.Controls.Panel container, IReadOnlyList<string> parts)
    {
        container.Children.Clear();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                var plusText = new System.Windows.Controls.TextBlock
                {
                    Text = "+",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                container.Children.Add(plusText);
            }

            var border = new System.Windows.Controls.Border
            {
                Style = (Style)FindResource("KeyCapBorderStyle"),
            };
            var text = new System.Windows.Controls.TextBlock
            {
                Text = parts[i],
                Style = (Style)FindResource("KeyCapTextStyle"),
            };
            border.Child = text;
            container.Children.Add(border);
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_settingsOpen)
        {
            return;
        }

        if (_activeInputWindow is { IsVisible: true } openBubble)
        {
            openBubble.FocusInput();
            return;
        }

        if (Interlocked.CompareExchange(ref _inputFlowActive, 1, 0) != 0)
        {
            return;
        }

        string? failureMessage = null;
        try
        {
            var target = _targetWindowService.CaptureForegroundWindow();
            if (target is null || !_targetWindowService.IsValidTarget(target, _options))
            {
                ShowTransientHintToast("当前不在 NoMachine 窗口内");
                return;
            }

            // 输入气泡不能以主窗口为 Owner：主窗口可见时，WPF 激活 Owner
            // 会把主窗口一并带到前台。气泡使用独立的 Topmost 窗口即可。
            var inputWindow = new InputWindow(
                target.BubbleAnchorPosition,
                _options.PasteBindings,
                _options.MaxInputLength,
                _unsentDraftText,
                _options.EnableVisualEffects)
            {
                ShowActivated = true,
            };

            _activeInputWindow = inputWindow;
            bool? result;
            try
            {
                result = inputWindow.ShowDialog();
            }
            finally
            {
                _activeInputWindow = null;
            }

            if (result != true || string.IsNullOrEmpty(inputWindow.EnteredText))
            {
                // 用户按 Esc 取消时，若输入框已有文字则暂存于内存，下次唤起气泡自动恢复；
                // 若用户已把输入框清空再按 Esc，则同步清空暂存草稿。
                _unsentDraftText = string.IsNullOrEmpty(inputWindow.EnteredText)
                    ? null
                    : inputWindow.EnteredText;
                return;
            }

            _unsentDraftText = null;
            var submittedText = inputWindow.EnteredText;
            var selectedAction = inputWindow.SelectedPasteAction ?? PasteShortcutAction.Enter;
            _lastSubmittedTarget = target;
            _lastSubmittedText = submittedText;
            _lastSubmittedPasteAction = selectedAction;

            Interlocked.Exchange(ref _retryQueued, 0);
            var toast = ShowStatusToast(target.BubbleAnchorPosition, "正在同步剪贴板并粘贴…");
            try
            {
                var coordinator = new InputBridgeCoordinator(
                    _targetWindowService,
                    _clipboardService,
                    _textInjectionService,
                    _options,
                    () => submittedText,
                    selectedAction,
                    _log);
                coordinator.OperationFailed += (_, msg) => failureMessage = msg;
                await coordinator.RunAsync(target);

                while (Interlocked.Exchange(ref _retryQueued, 0) != 0)
                {
                    failureMessage = await ExecuteRetryCoreAsync();
                }
            }
            finally
            {
                toast?.DismissSafely();
            }
        }
        catch (Exception exception)
        {
            failureMessage = $"无法打开输入窗口：{exception.Message}";
        }
        finally
        {
            Volatile.Write(ref _inputFlowActive, 0);
        }

        if (!string.IsNullOrEmpty(failureMessage))
        {
            OnOperationFailed(this, failureMessage);
        }
    }

    private async void OnRetryHotkeyPressed(object? sender, EventArgs e)
    {
        if (_settingsOpen || _activeInputWindow is { IsVisible: true })
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _inputFlowActive, 1, 0) != 0)
        {
            // 如果首次粘贴还在等待同步延迟期间，用户就按下了重试快捷键，记录排队并在当前轮结束后立即补发。
            Interlocked.Exchange(ref _retryQueued, 1);
            return;
        }

        string? failureMessage = null;
        try
        {
            do
            {
                Interlocked.Exchange(ref _retryQueued, 0);
                failureMessage = await ExecuteRetryCoreAsync();
            }
            while (Interlocked.Exchange(ref _retryQueued, 0) != 0);
        }
        catch (Exception exception)
        {
            failureMessage = $"无法重试粘贴：{exception.Message}";
        }
        finally
        {
            Volatile.Write(ref _inputFlowActive, 0);
        }

        if (!string.IsNullOrEmpty(failureMessage))
        {
            OnOperationFailed(this, failureMessage);
        }
    }

    private async Task<string?> ExecuteRetryCoreAsync()
    {
        var target = _lastSubmittedTarget;
        if (target is null || !_targetWindowService.IsValidTarget(target, _options))
        {
            var current = _targetWindowService.CaptureForegroundWindow();
            if (current is null || !_targetWindowService.IsValidTarget(current, _options))
            {
                ShowTransientHintToast("当前不在 NoMachine 窗口内");
                return null;
            }

            target = current;
        }

        // 如果上次在这回运行中提交过文字，完整复刻上一次提交的全流程（重新激活聚焦、重写剪贴板并等待同步、按上次槽位动作发送）；
        // 如果刚启动还没用气泡提交过文字，则直接对当前 NoMachine 窗口执行粘贴动作。
        string? failureMessage = null;
        var lastText = _lastSubmittedText;
        var action = _lastSubmittedPasteAction ?? PasteShortcutAction.Enter;
        var toast = ShowStatusToast(target.BubbleAnchorPosition, "正在重试粘贴…");
        try
        {
            var coordinator = new InputBridgeCoordinator(
                _targetWindowService,
                _clipboardService,
                _textInjectionService,
                _options,
                () => lastText,
                action,
                _log);
            coordinator.OperationFailed += (_, msg) => failureMessage = msg;
            if (!string.IsNullOrEmpty(lastText))
            {
                await coordinator.RunAsync(target);
            }
            else
            {
                await coordinator.RetryPasteAsync(target);
            }
        }
        finally
        {
            toast?.DismissSafely();
        }

        return failureMessage;
    }

    private StatusToastWindow? ShowStatusToast(
        ScreenPosition anchorPosition,
        string message,
        bool alwaysShow = false)
    {
        if (!alwaysShow && !_options.EnableVisualEffects)
        {
            return null;
        }

        try
        {
            var toast = new StatusToastWindow(anchorPosition, message);
            toast.Show();
            return toast;
        }
        catch
        {
            return null;
        }
    }

    private void ShowTransientHintToast(string message)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var toast = ShowStatusToast(new ScreenPosition(cursor.X, cursor.Y), message, alwaysShow: true);
        if (toast is null)
        {
            return;
        }

        _ = DismissAfterDelayAsync(toast, TimeSpan.FromMilliseconds(1200));
    }

    private static async Task DismissAfterDelayAsync(StatusToastWindow toast, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
        }
        finally
        {
            toast.DismissSafely();
        }
    }

    private void OnOperationFailed(object? sender, string message)
    {
        ConfirmDialog.ShowAlert(
            this,
            message,
            "NMTools - 输入桥接失败",
            isWarning: true);
    }

    private void ApplyInitialPlacement()
    {
        // 必须在窗口句柄创建之前设置：托盘启动时句柄先于显示创建，
        // 此后再交给 CenterScreen 或 Loaded 处理，窗口会先出现在默认位置再跳动。
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var savedSettings = _windowSettingsStore.Load();
        if (savedSettings is not null)
        {
            savedSettings = savedSettings with
            {
                Left = savedSettings.Left - 12,
                Top = savedSettings.Top - 12,
                Width = savedSettings.Width + 24,
                Height = savedSettings.Height + 24,
            };
        }

        var position = WindowPlacement.Resolve(
            savedSettings,
            new System.Windows.Size(Width, Height),
            SystemParameters.WorkArea,
            virtualScreen);
        Left = position.X;
        Top = position.Y;
    }

    private void SaveWindowSettings()
    {
        var settings = CaptureWindowSettings();
        if (settings is not null)
        {
            _windowSettingsStore.Save(settings);
        }
    }

    private void QueueWindowSettingsSave()
    {
        var settings = CaptureWindowSettings();
        if (settings is null)
        {
            return;
        }

        lock (_windowSettingsSaveLock)
        {
            var previousSave = _pendingWindowSettingsSave;
            _pendingWindowSettingsSave = Task.Run(async () =>
            {
                try
                {
                    await previousSave.ConfigureAwait(false);
                }
                catch
                {
                    // 上一次位置保存失败时，仍继续尝试保存最新位置。
                }

                try
                {
                    _windowSettingsStore.Save(settings);
                }
                catch
                {
                    // 位置记忆不是核心功能，后台保存失败不能影响窗口隐藏。
                }
            });
        }
    }

    private void WaitForPendingWindowSettingsSave()
    {
        Task pendingSave;
        lock (_windowSettingsSaveLock)
        {
            pendingSave = _pendingWindowSettingsSave;
        }

        try
        {
            pendingSave.GetAwaiter().GetResult();
        }
        catch
        {
            // 退出时仍会同步保存一次最新位置。
        }
    }

    private WindowSettings? CaptureWindowSettings()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized)
        {
            return null;
        }

        return new WindowSettings
        {
            Left = Left + 12,
            Top = Top + 12,
            Width = Width - 24,
            Height = Height - 24,
        };
    }

    private void DisposeHotkey()
    {
        if (_hotkeyService is null)
        {
            return;
        }

        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.RetryHotkeyPressed -= OnRetryHotkeyPressed;
        _hotkeyService.Dispose();
        _hotkeyService = null;
    }

    private static class NativeMethods
    {
        public const int SwRestore = 9;
        public const int WmNcLButtonDown = 0x00A1;
        public const int HtCaption = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
