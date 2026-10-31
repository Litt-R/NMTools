using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NMTools.App.Infrastructure;
using NMTools.App.Services;
using NMTools.Core;
using NMTools.Core.Updates;

namespace NMTools.App.Views;

public partial class SettingsWindow : Window
{
    private readonly InputBridgeOptions _currentOptions;
    private PasteShortcutBindings _pasteBindings;
    private HotkeyModifiers _globalHotkeyModifiers;
    private uint _globalHotkeyVirtualKey;
    private HotkeyModifiers _retryHotkeyModifiers;
    private uint _retryHotkeyVirtualKey;
    private const string CustomInputSlot = "CustomInput";
    private const string GlobalHotkeySlot = "GlobalHotkey";
    private const string RetryHotkeySlot = "RetryHotkey";

    private readonly AutoStartService _autoStart = AutoStartService.CreateDefault();
    private bool _autoStartInitial;

    private readonly UpdateService? _updateService;
    private string? _editingSlot;

    public SettingsWindow(InputBridgeOptions currentOptions, UpdateService? updateService = null)
    {
        ArgumentNullException.ThrowIfNull(currentOptions);
        _currentOptions = currentOptions;
        _updateService = updateService;
        _pasteBindings = currentOptions.PasteBindings;
        _globalHotkeyModifiers = currentOptions.HotkeyModifiers;
        _globalHotkeyVirtualKey = currentOptions.HotkeyVirtualKey;
        _retryHotkeyModifiers = currentOptions.RetryHotkeyModifiers;
        _retryHotkeyVirtualKey = currentOptions.RetryHotkeyVirtualKey;
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        var methods = CreatePasteMethodOptions();
        foreach (var comboBox in GetPasteMethodComboBoxes())
        {
            comboBox.ItemsSource = methods;
            ConfigureComboBoxSelection(comboBox);
        }

        LoadCurrentOptions();
    }

    /// <summary>
    /// 六个预置槽位及其粘贴方式下拉框的唯一对应表。新增槽位时只需要在这里加一行（并在 XAML 里加控件）。
    /// 顺序与界面从上到下一致。
    /// </summary>
    private IReadOnlyList<(PasteShortcutAction Action, System.Windows.Controls.ComboBox ComboBox)> GetPasteSlots()
    {
        return
        [
            (PasteShortcutAction.Enter, EnterPasteMethodComboBox),
            (PasteShortcutAction.ControlShiftV, ControlShiftVPasteMethodComboBox),
            (PasteShortcutAction.OpenCode, OpenCodePasteMethodComboBox),
            (PasteShortcutAction.Emacs, EmacsPasteMethodComboBox),
            (PasteShortcutAction.Gvim, GvimPasteMethodComboBox),
            (PasteShortcutAction.GenericTerminal, GenericTerminalPasteMethodComboBox),
        ];
    }

    private IEnumerable<System.Windows.Controls.ComboBox> GetPasteMethodComboBoxes()
    {
        return GetPasteSlots().Select(slot => slot.ComboBox);
    }

    /// <summary>
    /// 以界面当前选择为准，生成每个预置槽位的粘贴方式；某个下拉框没有选择时沿用已有值。
    /// 自定义槽位的方式由两侧是否都设置决定，不在这里改动。
    /// </summary>
    private PasteShortcutBindings ApplySelectedMethods(PasteShortcutBindings bindings)
    {
        foreach (var (action, comboBox) in GetPasteSlots())
        {
            if (comboBox.SelectedItem is PasteMethodOption option)
            {
                bindings = bindings.SetMethod(action, option.Method);
            }
        }

        return bindings;
    }

    public InputBridgeOptions? UpdatedOptions { get; private set; }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        CloseWithConfirmation();
        e.Handled = true;
    }

    private void OnCancelButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        e.Handled = true;
    }

    private void CloseWithConfirmation()
    {
        if (!HasUnsavedChanges())
        {
            DialogResult = false;
            return;
        }

        var result = ConfirmDialog.Show(this);
        if (result == ConfirmDialog.CustomDialogResult.Yes)
        {
            SaveChanges();
        }
        else if (result == ConfirmDialog.CustomDialogResult.No)
        {
            DialogResult = false;
        }
    }

    private PasteShortcutBindings GetCurrentUiBindings()
    {
        return ApplySelectedMethods(_pasteBindings);
    }

    private bool HasUnsavedChanges()
    {
        var currentDelayText = ((int)Math.Round(_currentOptions.PostActivationClipboardSettleDelay.TotalMilliseconds)).ToString();
        return _currentOptions.HotkeyModifiers != GetSelectedModifiers()
            || _currentOptions.HotkeyVirtualKey != GetSelectedVirtualKey()
            || _currentOptions.RetryHotkeyModifiers != _retryHotkeyModifiers
            || _currentOptions.RetryHotkeyVirtualKey != _retryHotkeyVirtualKey
            || !string.Equals(ClipboardSettleDelayTextBox.Text.Trim(), currentDelayText, StringComparison.Ordinal)
            || _currentOptions.EnableVisualEffects != (VisualEffectsCheckBox.IsChecked == true)
            || _currentOptions.CheckUpdatesOnStartup != (CheckUpdatesOnStartupCheckBox.IsChecked == true)
            || _currentOptions.PasteBindings != GetCurrentUiBindings()
            || (AutoStartCheckBox.IsChecked == true) != _autoStartInitial;
    }

    private HotkeyModifiers GetSelectedModifiers()
    {
        return _globalHotkeyModifiers;
    }

    private uint GetSelectedVirtualKey()
    {
        return _globalHotkeyVirtualKey;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        GlobalHotkeyButton.Focus();
    }

    private static void ConfigureComboBoxSelection(System.Windows.Controls.ComboBox comboBox)
    {
        comboBox.AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnComboBoxPreviewMouseLeftButtonDown));
    }

    private static void OnComboBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || FindVisualParent<ComboBoxItem>(source) is not ComboBoxItem item
            || item.DataContext is not object value
            || sender is not System.Windows.Controls.ComboBox comboBox)
        {
            return;
        }

        comboBox.SelectedItem = value;
        comboBox.IsDropDownOpen = false;
        e.Handled = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        PreviewKeyDown -= OnPreviewKeyDown;
    }

    private void OnOuterFramePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source) is not null
            || FindVisualParent<System.Windows.Controls.Primitives.Selector>(source) is not null
            || FindVisualParent<System.Windows.Controls.ComboBoxItem>(source) is not null
            || FindVisualParent<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null
            || FindVisualParent<System.Windows.Controls.Primitives.Popup>(source) is not null)
        {
            return;
        }

        OnDragAreaMouseLeftButtonDown(sender, e);
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

    private void OnDragAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        CancelShortcutEditing();
        CloseOpenDropDowns();

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // 无标题栏窗口不依赖 WPF DragMove 的鼠标捕获状态，改用标准 HTCAPTION 拖动消息，
        // 避免下拉 Popup 关闭后残留捕获导致连续拖动失效。
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(
            handle,
            NativeMethods.WmNcLButtonDown,
            new IntPtr(NativeMethods.HtCaption),
            IntPtr.Zero);
    }

    private void CloseOpenDropDowns()
    {
        SyncDelayHelpPopup.IsOpen = false;
        foreach (var comboBox in GetPasteMethodComboBoxes())
        {
            comboBox.IsDropDownOpen = false;
        }
    }

    private void OnClearCustomClick(object sender, RoutedEventArgs e)
    {
        _pasteBindings = _pasteBindings
            .ClearShortcut(PasteShortcutAction.Custom)
            .SetCustomInput(null);
        CancelShortcutEditing();
        e.Handled = true;
    }

    private void OnSyncDelayHelpClick(object sender, RoutedEventArgs e)
    {
        CancelShortcutEditing();
        SyncDelayHelpPopup.IsOpen = !SyncDelayHelpPopup.IsOpen;
        e.Handled = true;
    }

    private void OnDelayPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string presetValue })
        {
            ClipboardSettleDelayTextBox.Text = presetValue;
            SyncDelayHelpPopup.IsOpen = false;
        }

        e.Handled = true;
    }

    private void OnDelayTextBoxPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = string.IsNullOrEmpty(e.Text) || !e.Text.All(char.IsAsciiDigit);
    }

    private void OnDelayTextBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(typeof(string))
            || e.DataObject.GetData(typeof(string)) is not string text
            || string.IsNullOrEmpty(text)
            || !text.Trim().All(char.IsAsciiDigit))
        {
            e.CancelCommand();
        }
    }

    private void OnDelayTextBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        const int step = 50;
        var current = int.TryParse(ClipboardSettleDelayTextBox.Text.Trim(), out var parsed)
            ? parsed
            : InputBridgeOptions.DefaultClipboardSettleDelayMilliseconds;
        var delta = e.Delta > 0 ? step : -step;
        var next = Math.Clamp(
            (int)Math.Round((current + delta) / (double)step) * step,
            InputBridgeOptions.MinClipboardSettleDelayMilliseconds,
            InputBridgeOptions.MaxClipboardSettleDelayMilliseconds);
        ClipboardSettleDelayTextBox.Text = next.ToString();
        ClipboardSettleDelayTextBox.CaretIndex = ClipboardSettleDelayTextBox.Text.Length;
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editingSlot != CustomInputSlot
            || e.ChangedButton is not (MouseButton.Middle or MouseButton.Right))
        {
            return;
        }

        var modifiers = GetCurrentModifiers();
        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            ShowInfo("自定义输入不支持 Win 修饰键，请使用 Ctrl、Alt 或 Shift。", "NMTools - 输入无效");
            e.Handled = true;
            return;
        }

        var button = e.ChangedButton == MouseButton.Middle
            ? RemoteMouseButton.Middle
            : RemoteMouseButton.Right;
        _pasteBindings = _pasteBindings.SetCustomInput(new RemoteInput(modifiers, 0, button));
        _editingSlot = null;
        UpdateShortcutButtons();
        e.Handled = true;
    }

    private void HandleCustomInputKey(Key key, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        if (key == Key.Escape)
        {
            CancelShortcutEditing();
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            _pasteBindings = _pasteBindings.SetCustomInput(null);
            CancelShortcutEditing();
            return;
        }

        if (IsModifierKey(key) || key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }

        var modifiers = GetCurrentModifiers();
        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            ShowInfo("自定义输入不支持 Win 修饰键，请使用 Ctrl、Alt 或 Shift。", "NMTools - 输入无效");
            return;
        }

        var input = new RemoteInput(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), RemoteMouseButton.None);
        if (!input.IsValid())
        {
            ShowInfo("这个按键无法作为自定义输入，请换一个按键。", "NMTools - 输入无效");
            return;
        }

        _pasteBindings = _pasteBindings.SetCustomInput(input);
        _editingSlot = null;
        UpdateShortcutButtons();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_editingSlot is not null)
        {
            CancelShortcutEditing();
        }
    }

    private void CancelShortcutEditing()
    {
        _editingSlot = null;
        UpdateShortcutButtons();
    }

    private void LoadCurrentOptions()
    {
        ClipboardSettleDelayTextBox.Text =
            ((int)Math.Round(_currentOptions.PostActivationClipboardSettleDelay.TotalMilliseconds)).ToString();
        VisualEffectsCheckBox.IsChecked = _currentOptions.EnableVisualEffects;
        foreach (var (action, comboBox) in GetPasteSlots())
        {
            comboBox.SelectedValue = _pasteBindings.GetBinding(action).Method;
        }

        _autoStartInitial = _autoStart.IsEnabled();
        AutoStartCheckBox.IsChecked = _autoStartInitial;
        AutoStartCheckBox.IsEnabled = _autoStart.IsAvailable || _autoStartInitial;
        if (!_autoStart.IsAvailable)
        {
            AutoStartCheckBox.ToolTip = $"未找到 {AutoStartService.LauncherFileName}，请使用发布包运行后再设置";
        }

        UpdateShortcutButtons();
        CheckUpdatesOnStartupCheckBox.IsChecked = _currentOptions.CheckUpdatesOnStartup;
        CurrentVersionText.Text = $"当前 {UpdateDisplay.FormatVersion(AssemblyVersion)}";
        if (_updateService is null)
        {
            // UI 自动化或测试环境没有注入服务时禁用按钮，避免误发起真实网络请求。
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.ToolTip = "当前环境不可用";
        }
    }

    /// <summary>
    /// 当前程序集版本。与主窗口的 VersionLabel 同源，避免两处显示不一致。
    /// </summary>
    internal static Version AssemblyVersion =>
        typeof(SettingsWindow).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var updateService = _updateService;
        if (updateService is null || updateService.IsBusy)
        {
            return;
        }

        // 用按钮自身显示进度，不额外占用一行文字；
        // 窗口可能在等待期间被关闭，因此结果回写前先确认控件仍可用。
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";
        try
        {
            // 以设置窗口为 Owner：它是模态对话框，用主窗口做 Owner 的弹窗会被压在它后面。
            await updateService.CheckAsync(interactive: true, owner: this);
        }
        finally
        {
            if (IsLoaded)
            {
                CheckUpdateButton.Content = "检查更新";
                CheckUpdateButton.IsEnabled = true;
            }
        }
    }

    private void OnShortcutButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string slot)
        {
            return;
        }

        _editingSlot = slot;
        button.Content = slot == CustomInputSlot ? "请按键或点鼠标…" : "请按键…";
        button.Focus();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_editingSlot is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_editingSlot == CustomInputSlot)
        {
            HandleCustomInputKey(key, e);
            return;
        }

        if (_editingSlot == GlobalHotkeySlot)
        {
            HandleGlobalHotkeyKey(key, e);
            return;
        }

        if (_editingSlot == RetryHotkeySlot)
        {
            HandleRetryHotkeyKey(key, e);
            return;
        }

        if (key == Key.Escape)
        {
            CancelShortcutEditing();
            e.Handled = true;
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            var action = ToAction(_editingSlot);
            if (action is not null)
            {
                _pasteBindings = _pasteBindings.ClearShortcut(action.Value);
            }

            CancelShortcutEditing();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0 || !HotkeyValidation.IsVirtualKeyInRange((uint)virtualKey))
        {
            return;
        }

        var shortcut = new PasteShortcut(GetCurrentModifiers(), (uint)virtualKey);
        if (shortcut.Modifiers == HotkeyModifiers.None && key != Key.Enter)
        {
            ShowInfo("快捷键必须包含修饰键；只有 Enter 可以不带修饰键。", "NMTools - 快捷键无效");
            e.Handled = true;
            return;
        }

        if (shortcut.Modifiers == HotkeyModifiers.Shift && key == Key.Enter)
        {
            ShowInfo("Shift + Enter 保留用于输入框换行，请选择其他快捷键。", "NMTools - 快捷键无效");
            e.Handled = true;
            return;
        }

        var actionToEdit = ToAction(_editingSlot);
        if (actionToEdit is null || _pasteBindings.HasConflict(shortcut, actionToEdit))
        {
            ShowInfo("这个快捷键已经被其他功能占用，请选择其他组合键。", "NMTools - 快捷键冲突");
            e.Handled = true;
            return;
        }

        _pasteBindings = _pasteBindings.SetShortcut(actionToEdit.Value, shortcut);
        _editingSlot = null;
        UpdateShortcutButtons();
        e.Handled = true;
    }

    private void HandleGlobalHotkeyKey(Key key, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        if (key is Key.Escape or Key.Delete or Key.Back)
        {
            CancelShortcutEditing();
            return;
        }

        if (IsModifierKey(key) || key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0 || !HotkeyValidation.IsVirtualKeyInRange((uint)virtualKey))
        {
            return;
        }

        var modifiers = GetCurrentModifiers();
        if (!HotkeyValidation.IsValidGlobalHotkey(modifiers, (uint)virtualKey))
        {
            ShowInfo("唤起气泡全局快捷键必须包含至少一个修饰键（Ctrl、Alt、Shift 或 Win）。", "NMTools - 快捷键无效");
            return;
        }

        if (_retryHotkeyVirtualKey != 0
            && modifiers == _retryHotkeyModifiers
            && (uint)virtualKey == _retryHotkeyVirtualKey)
        {
            ShowInfo("唤起气泡快捷键不能与重试粘贴快捷键相同。", "NMTools - 快捷键冲突");
            return;
        }

        _globalHotkeyModifiers = modifiers;
        _globalHotkeyVirtualKey = (uint)virtualKey;
        _editingSlot = null;
        UpdateShortcutButtons();
    }

    private void HandleRetryHotkeyKey(Key key, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        if (key is Key.Escape or Key.Delete or Key.Back)
        {
            CancelShortcutEditing();
            return;
        }

        if (IsModifierKey(key) || key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0 || !HotkeyValidation.IsVirtualKeyInRange((uint)virtualKey))
        {
            return;
        }

        var modifiers = GetCurrentModifiers();
        if (!HotkeyValidation.IsValidGlobalHotkey(modifiers, (uint)virtualKey))
        {
            ShowInfo("重试粘贴全局快捷键必须包含至少一个修饰键（Ctrl、Alt、Shift 或 Win）。", "NMTools - 快捷键无效");
            return;
        }

        if (modifiers == GetSelectedModifiers() && (uint)virtualKey == GetSelectedVirtualKey())
        {
            ShowInfo("重试粘贴快捷键不能与唤起气泡的触发快捷键相同。", "NMTools - 快捷键冲突");
            return;
        }

        _retryHotkeyModifiers = modifiers;
        _retryHotkeyVirtualKey = (uint)virtualKey;
        _editingSlot = null;
        UpdateShortcutButtons();
    }

    private void ShowInfo(string message, string title)
    {
        ConfirmDialog.ShowAlert(this, message, title);
    }

    private static PasteShortcutAction? ToAction(string slot)
    {
        return PasteShortcutActionParser.TryParse(slot);
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private static HotkeyModifiers GetCurrentModifiers()
    {
        return Keyboard.Modifiers.ToHotkeyModifiers();
    }

    private void UpdateShortcutButtons()
    {
        GlobalHotkeyButton.Content = HotkeyValidation.IsValidGlobalHotkey(_globalHotkeyModifiers, _globalHotkeyVirtualKey)
            ? HotkeyFormatter.Format(new PasteShortcut(_globalHotkeyModifiers, _globalHotkeyVirtualKey))
            : "Ctrl + Alt + Space";
        GenericTerminalShortcutButton.Content = FormatShortcut(_pasteBindings.GenericTerminal.Shortcut, "未设置");
        EnterShortcutButton.Content = FormatShortcut(_pasteBindings.Enter.Shortcut, "Enter");
        ControlShiftVShortcutButton.Content = FormatShortcut(_pasteBindings.ControlShiftV.Shortcut, "Ctrl + Enter");
        OpenCodeShortcutButton.Content = FormatShortcut(_pasteBindings.OpenCode.Shortcut, "Ctrl + O");
        EmacsShortcutButton.Content = FormatShortcut(_pasteBindings.Emacs.Shortcut, "Ctrl + Y");
        GvimShortcutButton.Content = FormatShortcut(_pasteBindings.Gvim.Shortcut, "Ctrl + G");
        CustomShortcutButton.Content = _pasteBindings.Custom.Shortcut is null
            ? "未设置"
            : HotkeyFormatter.Format(_pasteBindings.Custom.Shortcut);
        CustomInputButton.Content = _pasteBindings.Custom.CustomInput is null
            ? "点击后按键或点鼠标"
            : HotkeyFormatter.Format(_pasteBindings.Custom.CustomInput);
        CustomClearButton.Visibility = _pasteBindings.Custom.Shortcut is null && _pasteBindings.Custom.CustomInput is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        RetryHotkeyButton.Content = HotkeyValidation.IsValidGlobalHotkey(_retryHotkeyModifiers, _retryHotkeyVirtualKey)
            ? HotkeyFormatter.Format(new PasteShortcut(_retryHotkeyModifiers, _retryHotkeyVirtualKey))
            : "Ctrl + Alt + V";
    }

    private static string FormatShortcut(PasteShortcut? shortcut, string fallback)
    {
        return shortcut is null ? fallback : HotkeyFormatter.Format(shortcut);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveChanges();
    }

    private void SaveChanges()
    {
        var modifiers = GetSelectedModifiers();
        var virtualKey = GetSelectedVirtualKey();
        if (!HotkeyValidation.IsValidGlobalHotkey(modifiers, virtualKey))
        {
            ShowInfo("请完整设置全局快捷键。", "NMTools - 设置无效");
            return;
        }

        if (_editingSlot is not null)
        {
            CancelShortcutEditing();
        }

        if (!HotkeyValidation.IsValidGlobalHotkey(_retryHotkeyModifiers, _retryHotkeyVirtualKey))
        {
            ShowInfo("请完整设置重试粘贴全局快捷键。", "NMTools - 设置无效");
            return;
        }

        if (_retryHotkeyVirtualKey != 0
            && _retryHotkeyModifiers == modifiers
            && _retryHotkeyVirtualKey == virtualKey)
        {
            ShowInfo("重试粘贴快捷键不能与唤起气泡的触发快捷键相同。", "NMTools - 快捷键冲突");
            return;
        }

        if (!int.TryParse(ClipboardSettleDelayTextBox.Text.Trim(), out var settleDelayMs)
            || !InputBridgeOptions.IsValidClipboardSettleDelayMilliseconds(settleDelayMs))
        {
            ShowInfo(
                $"剪贴板同步等待延迟请输入 {InputBridgeOptions.MinClipboardSettleDelayMilliseconds} 到 {InputBridgeOptions.MaxClipboardSettleDelayMilliseconds} 之间的整数（毫秒）。",
                "NMTools - 设置无效");
            return;
        }

        var custom = _pasteBindings.Custom;
        if ((custom.Shortcut is null) != (custom.CustomInput is null))
        {
            ShowInfo(
                "自定义行只设置了一侧：左侧快捷键和右侧按键需要同时设置才会生效。\n请补全另一侧，或点击右侧的 ✕ 清空该行后再保存。",
                "NMTools - 自定义未设置完整");
            return;
        }

        if (GetPasteSlots().Any(slot => slot.ComboBox.SelectedItem is not PasteMethodOption))
        {
            ShowInfo("请选择每个快捷键对应的粘贴行为。", "NMTools - 设置无效");
            return;
        }

        _pasteBindings = ApplySelectedMethods(_pasteBindings);

        if (!_pasteBindings.IsValid())
        {
            ShowInfo("快捷键配置无效：快捷键不能重复；自定义槽位需要左右两侧都设置才会生效。", "NMTools - 设置无效");
            return;
        }

        if (!ApplyAutoStart())
        {
            return;
        }

        UpdatedOptions = _currentOptions with
        {
            HotkeyModifiers = modifiers,
            HotkeyVirtualKey = virtualKey,
            RetryHotkeyModifiers = _retryHotkeyModifiers,
            RetryHotkeyVirtualKey = _retryHotkeyVirtualKey,
            PostActivationClipboardSettleDelay = TimeSpan.FromMilliseconds(settleDelayMs),
            EnableVisualEffects = VisualEffectsCheckBox.IsChecked == true,
            CheckUpdatesOnStartup = CheckUpdatesOnStartupCheckBox.IsChecked == true,
            PasteBindings = _pasteBindings,
        };
        DialogResult = true;
    }

    private bool ApplyAutoStart()
    {
        var wanted = AutoStartCheckBox.IsChecked == true;
        if (wanted == _autoStartInitial)
        {
            return true;
        }

        try
        {
            _autoStart.SetEnabled(wanted);
            _autoStartInitial = wanted;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException
                or System.Security.SecurityException or System.IO.IOException)
        {
            ShowInfo($"开机自启动设置失败：{exception.Message}", "NMTools - 开机自启动");
            return false;
        }
    }

    private static IReadOnlyList<PasteMethodOption> CreatePasteMethodOptions()
    {
        return new[]
        {
            new PasteMethodOption(PasteMethod.ControlV, "Claude (Ctrl + V)"),
            new PasteMethodOption(PasteMethod.ControlShiftV, "通用终端 (Ctrl + Shift + V)"),
            new PasteMethodOption(PasteMethod.ShiftMiddleClick, "OpenCode (Shift + 鼠标中键)"),
            new PasteMethodOption(PasteMethod.EmacsYank, "Emacs (Ctrl + Y)"),
            new PasteMethodOption(PasteMethod.GvimRegisterPaste, "gVim (\"+p)"),
            new PasteMethodOption(PasteMethod.MiddleClick, "Terminal (鼠标中键)"),
            new PasteMethodOption(PasteMethod.ClipboardOnly, "仅复制到剪贴板"),
            new PasteMethodOption(PasteMethod.None, "不执行任何动作"),
        };
    }

    private sealed record PasteMethodOption(PasteMethod Method, string DisplayName);

    private static class NativeMethods
    {
        public const int WmNcLButtonDown = 0x00A1;
        public const int HtCaption = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    }
}
