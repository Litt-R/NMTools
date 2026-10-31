using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NMTools.Core;
using Xunit;

namespace NMTools.UIAutomation.Tests;

public sealed class SettingsWindowUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly string[] OrderedOptionNames =
    [
        "Claude (Ctrl + V)",
        "通用终端 (Ctrl + Shift + V)",
        "OpenCode (Shift + 鼠标中键)",
        "Emacs (Ctrl + Y)",
        "gVim (",
        "Terminal (鼠标中键)",
        "仅复制到剪贴板",
        "不执行任何动作",
    ];

    [RequiresNmToolsAppFact]
    public void DefaultLayout_ShowsConfiguredShortcutsAndMethods()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        Assert.Equal("Enter", session.Find("EnterShortcutButton").Name);
        Assert.Equal("Ctrl + Enter", session.Find("ControlShiftVShortcutButton").Name);
        Assert.Equal("Ctrl + O", session.Find("OpenCodeShortcutButton").Name);
        Assert.Equal("Ctrl + Y", session.Find("EmacsShortcutButton").Name);
        Assert.Equal("Ctrl + G", session.Find("GvimShortcutButton").Name);
        Assert.Equal("Ctrl + T", session.Find("GenericTerminalShortcutButton").Name);
        Assert.Equal("未设置", session.Find("CustomShortcutButton").Name);
        Assert.Equal("点击后按键或点鼠标", session.Find("CustomInputButton").Name);
        Assert.Equal("Ctrl + Alt + Space", session.Find("GlobalHotkeyButton").Name);
        Assert.Equal("Ctrl + Alt + V", session.Find("RetryHotkeyButton").Name);
        Assert.Equal("400", session.Find("ClipboardSettleDelayTextBox").AsTextBox().Text);
        Assert.NotNull(session.TryFind("SyncDelayHelpButton"));

        Assert.Contains("Claude (Ctrl + V)", session.SelectedName("EnterPasteMethodComboBox"));
        Assert.Contains("通用终端 (Ctrl + Shift + V)", session.SelectedName("ControlShiftVPasteMethodComboBox"));
        Assert.Contains("OpenCode (Shift + 鼠标中键)", session.SelectedName("OpenCodePasteMethodComboBox"));
        Assert.Contains("Emacs (Ctrl + Y)", session.SelectedName("EmacsPasteMethodComboBox"));
        Assert.Contains("gVim", session.SelectedName("GvimPasteMethodComboBox"));
        Assert.Contains("Terminal (鼠标中键)", session.SelectedName("GenericTerminalPasteMethodComboBox"));

        Assert.Equal(ToggleState.Off, session.Find("AutoStartCheckBox").AsCheckBox().ToggleState);
        Assert.Null(session.TryFind("CustomClearButton"));
    }

    [RequiresNmToolsAppFact]
    public void DropDownList_ListsOptionsInSettingsOrder()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        var items = session.ExpandAndGetItems(session.Combo("EnterPasteMethodComboBox"));

        Assert.Equal(OrderedOptionNames.Length, items.Count);
        for (var i = 0; i < OrderedOptionNames.Length; i++)
        {
            Assert.Contains(OrderedOptionNames[i], items[i].Name);
        }
    }

    [RequiresNmToolsAppFact]
    public void DropDownPopup_IsAlignedWithComboBox()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        foreach (var id in new[] { "EnterPasteMethodComboBox", "GenericTerminalPasteMethodComboBox" })
        {
            var combo = session.Combo(id);
            var comboRect = combo.BoundingRectangle;
            var items = session.ExpandAndGetItems(combo);

            foreach (var item in items)
            {
                var rect = item.BoundingRectangle;
                Assert.True(
                    rect.Left >= comboRect.Left && rect.Left - comboRect.Left <= 24,
                    $"{id}: popup item left edge {rect.Left} is not aligned with combo box left edge {comboRect.Left}.");
                Assert.True(
                    rect.Right <= comboRect.Right + 1 && comboRect.Right - rect.Right <= 24,
                    $"{id}: popup item right edge {rect.Right} is not aligned with combo box right edge {comboRect.Right}.");
            }

            combo.Collapse();
        }
    }

    [RequiresNmToolsAppFact]
    public void SelectingMethodAndSaving_PersistsChoice()
    {
        using var env = new IsolatedAppEnvironment();
        using (var session = env.LaunchSettings())
        {
            session.SelectOption("EnterPasteMethodComboBox", "通用终端 (Ctrl + Shift + V)");
            session.ClickSave();
        }

        using var saved = env.ReadSettings();
        Assert.Equal((int)PasteMethod.ControlShiftV, MethodOf(saved, "Enter"));
    }

    [RequiresNmToolsAppFact]
    public void FirstDropDownItem_CanBeSelected()
    {
        using var env = new IsolatedAppEnvironment();
        using (var session = env.LaunchSettings())
        {
            session.SelectOption("GenericTerminalPasteMethodComboBox", "Claude (Ctrl + V)");
            session.ClickSave();
        }

        using var saved = env.ReadSettings();
        Assert.Equal((int)PasteMethod.ControlV, MethodOf(saved, "GenericTerminal"));
    }

    [RequiresNmToolsAppFact]
    public void CustomRow_OneSidedSave_ShowsPromptAndDoesNotSave()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        session.EnsureSettingsWindowIsForeground();
        session.Invoke("CustomShortcutButton");
        Assert.Equal("请按键…", session.Find("CustomShortcutButton").Name);
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        IsolatedAppEnvironment.WaitFor(() => session.Find("CustomShortcutButton").Name == "Ctrl + K", Timeout);

        session.ClickSaveExpectingPrompt();
        var prompt = session.WaitForDialog("NMTools - 自定义未设置完整");
        prompt.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))!
            .AsButton().Invoke();

        Assert.False(File.Exists(env.SettingsPath));
        Assert.False(session.HasExited);
        Assert.NotNull(session.TryFind("CustomClearButton"));
    }

    [RequiresNmToolsAppFact]
    public void CustomRow_ClearButton_ResetsBothSides()
    {
        using var env = new IsolatedAppEnvironment();
        env.SeedSettings(new PasteShortcutBindings()
            .SetShortcut(PasteShortcutAction.Custom, new PasteShortcut(HotkeyModifiers.Control, 0x4B))
            .SetCustomInput(new RemoteInput(HotkeyModifiers.Shift, 0, RemoteMouseButton.Middle)));
        using (var session = env.LaunchSettings())
        {
            Assert.Equal("Ctrl + K", session.Find("CustomShortcutButton").Name);
            Assert.Equal("Shift + 鼠标中键", session.Find("CustomInputButton").Name);

            session.Invoke("CustomClearButton");

            IsolatedAppEnvironment.WaitFor(() => session.Find("CustomShortcutButton").Name == "未设置", Timeout);
            Assert.Equal("点击后按键或点鼠标", session.Find("CustomInputButton").Name);
            IsolatedAppEnvironment.WaitFor(() => session.TryFind("CustomClearButton") is null, Timeout);

            session.ClickSave();
        }

        using var saved = env.ReadSettings();
        var custom = saved.RootElement.GetProperty("PasteBindings").GetProperty("Custom");
        Assert.Equal(JsonValueKind.Null, custom.GetProperty("Shortcut").ValueKind);
        Assert.Equal(JsonValueKind.Null, custom.GetProperty("CustomInput").ValueKind);
        Assert.Equal((int)PasteMethod.None, custom.GetProperty("Method").GetInt32());
    }

    [RequiresNmToolsAppFact]
    public void CustomRow_KeyboardRecording_BothSidesTakeEffect()
    {
        using var env = new IsolatedAppEnvironment();
        using (var session = env.LaunchSettings())
        {
            session.EnsureSettingsWindowIsForeground();

            session.Invoke("CustomShortcutButton");
            Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
            IsolatedAppEnvironment.WaitFor(() => session.Find("CustomShortcutButton").Name == "Ctrl + K", Timeout);

            session.Invoke("CustomInputButton");
            Assert.Equal("请按键或点鼠标…", session.Find("CustomInputButton").Name);
            Press(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
            IsolatedAppEnvironment.WaitFor(() => session.Find("CustomInputButton").Name == "Ctrl + Alt + P", Timeout);

            session.ClickSave();
        }

        using var saved = env.ReadSettings();
        var custom = saved.RootElement.GetProperty("PasteBindings").GetProperty("Custom");
        Assert.Equal((int)PasteMethod.CustomInput, custom.GetProperty("Method").GetInt32());
        Assert.Equal(0x4B, custom.GetProperty("Shortcut").GetProperty("VirtualKey").GetInt32());
        var input = custom.GetProperty("CustomInput");
        Assert.Equal(0x50, input.GetProperty("VirtualKey").GetInt32());
        Assert.Equal((int)(HotkeyModifiers.Control | HotkeyModifiers.Alt), input.GetProperty("Modifiers").GetInt32());
        Assert.Equal((int)RemoteMouseButton.None, input.GetProperty("MouseButton").GetInt32());
    }

    [RequiresNmToolsAppFact]
    public void CustomRow_ShiftMiddleClickRecording_IsStored()
    {
        using var env = new IsolatedAppEnvironment();
        using (var session = env.LaunchSettings())
        {
            session.EnsureSettingsWindowIsForeground();

            session.Invoke("CustomInputButton");
            var target = session.Find("CustomInputButton").GetClickablePoint();
            Keyboard.Press(VirtualKeyShort.SHIFT);
            try
            {
                Mouse.Click(target, MouseButton.Middle);
            }
            finally
            {
                Keyboard.Release(VirtualKeyShort.SHIFT);
            }

            IsolatedAppEnvironment.WaitFor(
                () => session.Find("CustomInputButton").Name == "Shift + 鼠标中键",
                Timeout,
                $"Middle-click was not recorded; button shows '{session.Find("CustomInputButton").Name}', target point {target}.");
            Assert.NotNull(session.TryFind("CustomClearButton"));

            session.EnsureSettingsWindowIsForeground();
            session.Invoke("CustomShortcutButton");
            IsolatedAppEnvironment.WaitFor(
                () => session.Find("CustomShortcutButton").Name == "请按键…",
                Timeout,
                "The left side did not enter recording mode.");
            Thread.Sleep(300);
            Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
            IsolatedAppEnvironment.WaitFor(
                () => session.Find("CustomShortcutButton").Name == "Ctrl + K",
                Timeout,
                $"Left side shows '{session.Find("CustomShortcutButton").Name}' instead of 'Ctrl + K'.");

            session.ClickSave();
        }

        using var saved = env.ReadSettings();
        var input = saved.RootElement.GetProperty("PasteBindings").GetProperty("Custom").GetProperty("CustomInput");
        Assert.Equal((int)RemoteMouseButton.Middle, input.GetProperty("MouseButton").GetInt32());
        Assert.Equal((int)HotkeyModifiers.Shift, input.GetProperty("Modifiers").GetInt32());
        Assert.Equal(0, input.GetProperty("VirtualKey").GetInt32());
    }

    [RequiresNmToolsAppFact]
    public void CloseWithoutChanges_ClosesImmediately()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        session.Invoke("SettingsCloseButton");

        IsolatedAppEnvironment.WaitFor(() => session.HasExited, Timeout, "The app did not exit after closing an unmodified settings window.");
        Assert.False(File.Exists(env.SettingsPath));
    }

    [RequiresNmToolsAppFact]
    public void CloseWithChanges_AsksAndDiscardsWhenDeclined()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        session.SelectOption("EnterPasteMethodComboBox", "Emacs (Ctrl + Y)");
        session.Invoke("SettingsCloseButton");

        var dialog = session.WaitForDialog("NMTools - 保存设置");
        dialog.FindFirstDescendant(cf => cf.ByName("不保存"))!.AsButton().Invoke();

        IsolatedAppEnvironment.WaitFor(() => session.HasExited, Timeout);
        Assert.False(File.Exists(env.SettingsPath));
    }

    [RequiresNmToolsAppFact]
    public void AutoStart_IsDisabledWhenLauncherIsMissing()
    {
        using var env = new IsolatedAppEnvironment(withLauncher: false);
        using var session = env.LaunchSettings();

        var checkBox = session.Find("AutoStartCheckBox");
        Assert.False(checkBox.IsEnabled);
        Assert.Equal(ToggleState.Off, checkBox.AsCheckBox().ToggleState);
    }

    [RequiresNmToolsAppFact]
    public void AutoStart_EnableAndDisable_UpdatesStartupShortcutWithWindowlessCommand()
    {
        using var env = new IsolatedAppEnvironment(withLauncher: true);
        Assert.Null(env.ReadAutoStartShortcut());

        using (var session = env.LaunchSettings())
        {
            var checkBox = session.Find("AutoStartCheckBox");
            Assert.True(checkBox.IsEnabled);
            Assert.Equal(ToggleState.Off, checkBox.AsCheckBox().ToggleState);

            checkBox.AsCheckBox().IsChecked = true;
            session.ClickSave();
        }

        IsolatedAppEnvironment.WaitFor(() => env.ReadAutoStartShortcut() is not null, Timeout);
        var shortcut = env.ReadAutoStartShortcut();
        Assert.Equal("wscript.exe", Path.GetFileName(shortcut!.Value.Target), ignoreCase: true);
        // 末尾的 --tray 让开机自启动只驻留托盘、不弹主窗口。
        Assert.Equal($"//B //Nologo \"{env.LauncherPath}\" --tray", shortcut.Value.Arguments, ignoreCase: true);

        using (var session = env.LaunchSettings())
        {
            var checkBox = session.Find("AutoStartCheckBox").AsCheckBox();
            Assert.Equal(ToggleState.On, checkBox.ToggleState);

            checkBox.IsChecked = false;
            session.ClickSave();
        }

        IsolatedAppEnvironment.WaitFor(() => env.ReadAutoStartShortcut() is null, Timeout);
    }

    [RequiresNmToolsAppFact]
    public void AutoStart_ToggleThenDecline_DoesNotCreateStartupShortcut()
    {
        using var env = new IsolatedAppEnvironment(withLauncher: true);
        using var session = env.LaunchSettings();

        session.Find("AutoStartCheckBox").AsCheckBox().IsChecked = true;
        session.Invoke("SettingsCloseButton");
        var dialog = session.WaitForDialog("NMTools - 保存设置");
        dialog.FindFirstDescendant(cf => cf.ByName("不保存"))!.AsButton().Invoke();

        IsolatedAppEnvironment.WaitFor(() => session.HasExited, Timeout);
        Assert.Null(env.ReadAutoStartShortcut());
    }

    [RequiresNmToolsAppFact]
    public void SettingsWindow_KeepsAllRowsReachable()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        // 设置窗口加了「自动更新」分组后会变高，必须确认没有把既有的行挤出屏幕或挤出可滚动区域，
        // 否则用户（和下面这些依赖真实键盘输入的测试）会点不到控件。
        var window = session.Window.BoundingRectangle;
        var screen = System.Windows.Forms.Screen.FromRectangle(
            new System.Drawing.Rectangle(
                (int)window.Left,
                (int)window.Top,
                (int)window.Width,
                (int)window.Height)).WorkingArea;

        Assert.True(window.Height <= screen.Height, $"设置窗口高 {window.Height}，超出屏幕可用高度 {screen.Height}。");
        Assert.True(window.Top >= screen.Top - 1, "设置窗口顶部超出屏幕工作区。");
        Assert.True(window.Bottom <= screen.Bottom + 1, "设置窗口底部超出屏幕工作区，保存按钮可能点不到。");
    }

    [RequiresNmToolsAppFact]
    public void SettingsWindow_HelpTextIsNotClippedByControls()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        // 每一行都是「说明文字在左、控件在右」的两列布局。说明写长一点就会被右列压住，
        // 出现肉眼可见的文字截断。这里逐行断言两者的边界不相交。
        var rows = new (string TextId, string ControlId)[]
        {
            ("GlobalHotkeyHelpText", "GlobalHotkeyButton"),
            ("RetryHotkeyHelpText", "RetryHotkeyButton"),
        };

        foreach (var (textId, controlId) in rows)
        {
            var text = session.TryFind(textId)
                ?? throw new InvalidOperationException($"找不到说明文字 {textId}");
            var control = session.Find(controlId);

            var textRect = text.BoundingRectangle;
            var controlRect = control.BoundingRectangle;

            Assert.True(
                textRect.Right <= controlRect.Left + 1,
                $"{textId} 的右边界 {textRect.Right} 与 {controlId} 的左边界 {controlRect.Left} 重叠，文字会被遮挡。");
        }
    }

    [RequiresNmToolsAppFact]
    public void UpdateGroup_ShowsCurrentVersionAndDefaultsToUnchecked()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        var versionText = session.Find("CurrentVersionText").Name;
        Assert.Matches(@"^当前 v\d+\.\d+", versionText);

        var checkBox = session.Find("CheckUpdatesOnStartupCheckBox").AsCheckBox();
        Assert.Equal(ToggleState.Off, checkBox.ToggleState);

        // UI 自动化环境不注入更新服务，按钮必须禁用：测试不得发起真实网络请求。
        var button = session.Find("CheckUpdateButton");
        Assert.False(button.IsEnabled);
    }

    [RequiresNmToolsAppFact]
    public void UpdateGroup_EnablingPersistsFlag()
    {
        using var env = new IsolatedAppEnvironment();
        using (var session = env.LaunchSettings())
        {
            session.Find("CheckUpdatesOnStartupCheckBox").AsCheckBox().IsChecked = true;
            session.ClickSave();
        }

        using var saved = env.ReadSettings();
        Assert.True(saved.RootElement.GetProperty("CheckUpdatesOnStartup").GetBoolean());

        // 重开设置窗口时勾选状态必须回读，否则用户会以为设置没保存。
        using var reopened = env.LaunchSettings();
        Assert.Equal(ToggleState.On, reopened.Find("CheckUpdatesOnStartupCheckBox").AsCheckBox().ToggleState);
    }

    [RequiresNmToolsAppFact]
    public void UpdateGroup_ToggleThenDecline_DoesNotPersist()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchSettings();

        session.Find("CheckUpdatesOnStartupCheckBox").AsCheckBox().IsChecked = true;
        session.Invoke("SettingsCloseButton");
        var dialog = session.WaitForDialog("NMTools - 保存设置");
        dialog.FindFirstDescendant(cf => cf.ByName("不保存"))!.AsButton().Invoke();

        IsolatedAppEnvironment.WaitFor(() => session.HasExited, Timeout);
        Assert.False(File.Exists(env.SettingsPath));
    }

    private static int MethodOf(JsonDocument document, string slot)
    {
        return document.RootElement
            .GetProperty("PasteBindings")
            .GetProperty(slot)
            .GetProperty("Method")
            .GetInt32();
    }

    private static void Press(params VirtualKeyShort[] keys)
    {
        Keyboard.TypeSimultaneously(keys);
    }
}
