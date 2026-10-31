using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Microsoft.Win32;
using NMTools.Core;
using Xunit;

namespace NMTools.UIAutomation.Tests;

/// <summary>
/// 把应用复制到系统临时目录下的独立文件夹，每次启动都只显示设置窗口，并在释放时清理目录和测试用注册表项。
/// </summary>
internal sealed class IsolatedAppEnvironment : IDisposable
{
    private const string LauncherFileName = "Start-NMTools.vbs";
    private readonly string _sourceAppPath;
    private readonly string _guid = Guid.NewGuid().ToString("N");

    public IsolatedAppEnvironment(bool withLauncher = false)
    {
        _sourceAppPath = Environment.GetEnvironmentVariable("NMTOOLS_APP_PATH")
            ?? throw new InvalidOperationException("NMTOOLS_APP_PATH is not set.");
        Assert.True(File.Exists(_sourceAppPath), $"NMTools.App.exe was not found at '{_sourceAppPath}'.");

        RootDirectory = Path.Combine(Path.GetTempPath(), "NMTools.UiAutomation", _guid);
        AppDirectory = Path.Combine(RootDirectory, "program");
        Directory.CreateDirectory(AppDirectory);
        CopyDirectory(Path.GetDirectoryName(_sourceAppPath)!, AppDirectory);

        LauncherPath = Path.Combine(RootDirectory, LauncherFileName);
        if (withLauncher)
        {
            File.WriteAllText(LauncherPath, "' ui test launcher placeholder");
        }
    }

    public string RootDirectory { get; }

    public string AppDirectory { get; }

    public string LauncherPath { get; }

    public string SettingsPath => Path.Combine(AppDirectory, ".ui-test-data", "input-bridge-settings.json");

    public string WindowSettingsPath => Path.Combine(AppDirectory, ".ui-test-data", "window-settings.json");

    public string AutoStartShortcutPath => Path.Combine(RootDirectory, "startup", "NMTools.lnk");

    public (string Target, string Arguments)? ReadAutoStartShortcut()
    {
        if (!File.Exists(AutoStartShortcutPath))
        {
            return null;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(AutoStartShortcutPath);
            try
            {
                return ((string)shortcut.TargetPath, (string)shortcut.Arguments);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public void SeedSettings(PasteShortcutBindings bindings)
    {
        var defaults = new InputBridgeOptions();
        var settings = new
        {
            Version = 6,
            HotkeyModifiers = defaults.HotkeyModifiers,
            HotkeyVirtualKey = defaults.HotkeyVirtualKey,
            MaxInputLength = defaults.MaxInputLength,
            PasteBindings = bindings,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
    }

    public JsonDocument ReadSettings()
    {
        WaitFor(() => File.Exists(SettingsPath), TimeSpan.FromSeconds(5));
        return JsonDocument.Parse(File.ReadAllText(SettingsPath));
    }

    public MainWindowSession LaunchMainWindow()
    {
        var exePath = Path.Combine(AppDirectory, Path.GetFileName(_sourceAppPath));
        Directory.CreateDirectory(Path.Combine(AppDirectory, ".ui-test-data"));
        File.WriteAllText(
            WindowSettingsPath,
            "{\"Version\":1,\"Left\":0,\"Top\":0,\"Width\":500,\"Height\":280}");
        var startInfo = new ProcessStartInfo(exePath) { UseShellExecute = false };
        startInfo.Environment["NMTOOLS_UI_AUTOMATION"] = "1";
        var application = Application.Launch(startInfo);
        var automation = new UIA3Automation();
        try
        {
            var window = WaitForWindow(application, automation, "NMTools - 输入桥接", TimeSpan.FromSeconds(15));
            var session = new MainWindowSession(application, automation, window, exePath);
            session.EnsureWindowIsForeground();
            return session;
        }
        catch
        {
            automation.Dispose();
            SettingsSession.StopApplication(application, exePath);
            throw;
        }
    }

    public SettingsSession LaunchSettings()
    {
        var exePath = Path.Combine(AppDirectory, Path.GetFileName(_sourceAppPath));
        var startInfo = new ProcessStartInfo(exePath, "--ui-test-settings") { UseShellExecute = false };
        startInfo.Environment["NMTOOLS_UI_AUTOMATION"] = "1";
        var application = Application.Launch(startInfo);
        var automation = new UIA3Automation();
        try
        {
            var window = WaitForWindow(application, automation, "NMTools 设置", TimeSpan.FromSeconds(15));
            var session = new SettingsSession(application, automation, window, exePath);
            session.EnsureSettingsWindowIsForeground();
            return session;
        }
        catch
        {
            automation.Dispose();
            SettingsSession.StopApplication(application, exePath);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\NMTools.UiAutomation\{_guid}", throwOnMissingSubKey: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 清理失败不能掩盖测试结果。
        }

        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不能掩盖测试结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 清理失败不能掩盖测试结果。
        }
    }

    internal static Window WaitForWindow(Application application, UIA3Automation automation, string name, TimeSpan timeout)
    {
        var window = WaitForElement(
            () => application.GetAllTopLevelWindows(automation)
                .FirstOrDefault(candidate => candidate.Title == name)
                ?? automation.GetDesktop()
                    .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                    .Select(element => element.AsWindow())
                    .FirstOrDefault(candidate => candidate.Title == name),
            timeout);
        return window ?? throw new TimeoutException($"Window '{name}' did not appear.");
    }

    internal static T? WaitForElement<T>(Func<T?> find, TimeSpan timeout)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var element = find();
            if (element is not null)
            {
                return element;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    internal static void WaitFor(Func<bool> condition, TimeSpan timeout, string? message = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(100);
        }

        Assert.True(condition(), message ?? "The expected UI state was not reached before the timeout.");
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destinationSubdirectory = Path.Combine(destinationDirectory, Path.GetFileName(subdirectory));
            Directory.CreateDirectory(destinationSubdirectory);
            CopyDirectory(subdirectory, destinationSubdirectory);
        }
    }
}

internal sealed class MainWindowSession : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly Application _application;
    private readonly string _exePath;

    public MainWindowSession(Application application, UIA3Automation automation, Window window, string exePath)
    {
        _application = application;
        Automation = automation;
        Window = window;
        _exePath = exePath;
    }

    public UIA3Automation Automation { get; }

    public Window Window { get; }

    public FlaUI.Core.AutomationElements.AutomationElement Find(string automationId)
    {
        return Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?? throw new InvalidOperationException($"Element '{automationId}' was not found.");
    }

    public void EnsureWindowIsForeground()
    {
        Window.SetForeground();
        var handle = Window.Properties.NativeWindowHandle.ValueOrDefault;
        IsolatedAppEnvironment.WaitFor(
            () => NativeMethods.GetForegroundWindow() == handle,
            Timeout,
            "The main window is not in the foreground; refusing to send mouse input to another window.");
    }

    public void DragSurface(int startX, int startY, int deltaX, int deltaY)
    {
        EnsureWindowIsForeground();
        var bounds = Window.BoundingRectangle;
        var start = new System.Drawing.Point((int)bounds.Left + startX, (int)bounds.Top + startY);
        var end = new System.Drawing.Point(start.X + deltaX, start.Y + deltaY);
        Mouse.MoveTo(start);
        Mouse.Down(MouseButton.Left);
        try
        {
            Mouse.MoveTo(end);
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
        }

        IsolatedAppEnvironment.WaitFor(
            () => Math.Abs(Window.BoundingRectangle.Left - (bounds.Left + deltaX)) < 8
                && Math.Abs(Window.BoundingRectangle.Top - (bounds.Top + deltaY)) < 8,
            Timeout,
            $"The main window did not move after dragging point ({startX}, {startY}).");
    }

    public void ClickCloseButton()
    {
        EnsureWindowIsForeground();
        var closeButton = Window.FindFirstDescendant(cf => cf.ByAutomationId("MainWindowCloseButton"))
            ?? throw new InvalidOperationException("The main window close button was not found.");
        closeButton.Click();
    }

    public bool WaitUntilHidden()
    {
        var handle = Window.Properties.NativeWindowHandle.ValueOrDefault;
        var isHidden = false;
        IsolatedAppEnvironment.WaitFor(
            () => isHidden = !NativeMethods.IsWindowVisible(handle),
            Timeout,
            "The main window did not become hidden after the close button was clicked.");
        return isHidden;
    }

    public void Dispose()
    {
        Automation.Dispose();
        SettingsSession.StopApplication(_application, _exePath);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr handle);
    }
}

internal sealed class SettingsSession : IDisposable
{
    private readonly Application _application;
    private readonly string _exePath;

    public SettingsSession(Application application, UIA3Automation automation, Window window, string exePath)
    {
        _application = application;
        Automation = automation;
        Window = window;
        _exePath = exePath;
    }

    public UIA3Automation Automation { get; }

    public Window Window { get; }

    public bool HasExited => _application.HasExited;

    public AutomationElement Find(string automationId)
    {
        return Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?? throw new InvalidOperationException($"Element '{automationId}' was not found.");
    }

    public AutomationElement? TryFind(string automationId)
    {
        return Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
    }

    public FlaUI.Core.AutomationElements.ComboBox Combo(string automationId) => Find(automationId).AsComboBox();

    public void Invoke(string automationId) => Find(automationId).AsButton().Invoke();

    public IReadOnlyList<AutomationElement> ExpandAndGetItems(FlaUI.Core.AutomationElements.ComboBox comboBox)
    {
        comboBox.Expand();
        IReadOnlyList<AutomationElement> items = [];
        IsolatedAppEnvironment.WaitFor(
            () =>
            {
                items = GetPopupItems();
                return items.Count > 0;
            },
            TimeSpan.FromSeconds(5),
            "The drop-down list did not open.");
        return items;
    }

    public void SelectOption(string comboAutomationId, string displayName)
    {
        var comboBox = Combo(comboAutomationId);
        var items = ExpandAndGetItems(comboBox);
        var option = items.FirstOrDefault(item => item.Name.Contains(displayName, StringComparison.Ordinal));
        Assert.NotNull(option);
        option!.Click();
        IsolatedAppEnvironment.WaitFor(
            () => comboBox.SelectedItem?.Name.Contains(displayName, StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(5),
            $"'{displayName}' was not selected in {comboAutomationId}.");
    }

    public string SelectedName(string comboAutomationId) => Combo(comboAutomationId).SelectedItem?.Name ?? string.Empty;

    public void ClickSaveExpectingPrompt() =>
        Window.FindFirstDescendant(cf => cf.ByName("保存").And(cf.ByControlType(ControlType.Button)))!.AsButton().Invoke();

    public void ClickSave()
    {
        ClickSaveExpectingPrompt();
        IsolatedAppEnvironment.WaitFor(
            () => HasExited,
            TimeSpan.FromSeconds(10),
            "The app did not exit after saving; the save was probably rejected by a prompt.");
    }

    public Window WaitForDialog(string title)
    {
        // 对话框以设置窗口为 Owner，UI Automation 会把它挂在设置窗口下，而不是桌面顶层。
        var dialog = IsolatedAppEnvironment.WaitForElement(
            () => Window.ModalWindows.FirstOrDefault(candidate => candidate.Title == title)
                ?? Window.FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.Window)))?.AsWindow()
                ?? _application.GetAllTopLevelWindows(Automation).FirstOrDefault(candidate => candidate.Title == title),
            TimeSpan.FromSeconds(5));
        return dialog ?? throw new TimeoutException($"Dialog '{title}' did not appear.");
    }

    public void EnsureSettingsWindowIsForeground()
    {
        Window.SetForeground();
        var handle = Window.Properties.NativeWindowHandle.ValueOrDefault;
        IsolatedAppEnvironment.WaitFor(
            () => NativeMethods.GetForegroundWindow() == handle,
            TimeSpan.FromSeconds(5),
            "The settings window is not in the foreground; refusing to send keyboard input to another window.");
    }

    public void Dispose()
    {
        Automation.Dispose();
        StopApplication(_application, _exePath);
    }

    internal static void StopApplication(Application application, string executablePath)
    {
        try
        {
            var processIds = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath))
                .Where(process =>
                {
                    try
                    {
                        return string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return false;
                    }
                })
                .Select(process =>
                {
                    var processId = process.Id;
                    process.Dispose();
                    return processId;
                })
                .ToHashSet();
            processIds.Add(application.ProcessId);

            foreach (var processId in processIds)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(TimeSpan.FromSeconds(5));
                    }
                }
                catch (ArgumentException)
                {
                    // 进程已经退出。
                }
            }
        }
        finally
        {
            application.Dispose();
        }
    }

    private IReadOnlyList<AutomationElement> GetPopupItems()
    {
        // 弹出层会同时出现在设置窗口子树和顶层窗口列表中，按名称去重。
        return _application.GetAllTopLevelWindows(Automation)
            .SelectMany(window => window.FindAllDescendants())
            .Where(element => element.ControlType == ControlType.ListItem
                && element.Name.StartsWith("PasteMethodOption", StringComparison.Ordinal))
            .GroupBy(element => element.Name)
            .Select(group => group.First())
            .OrderBy(element => element.BoundingRectangle.Top)
            .ToList();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
    }
}
