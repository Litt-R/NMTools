using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NMTools.App.Infrastructure;

/// <summary>
/// 通过当前用户启动文件夹里的 NMTools.lnk 管理开机自启动。快捷方式运行发布包的 Start-NMTools.vbs，
/// 由它以隐藏窗口方式运行主程序，因此没有控制台黑框；快捷方式附带 --tray，程序启动后只驻留托盘、不显示主窗口。
/// 任务管理器“启动应用”页会把该项显示为 wscript.exe 的文件描述，而不是 NMTools，这是已知且接受的限制。
/// 旧版本写入的 Run 注册表项会在启用、关闭和启动刷新时被迁移或清除。
/// </summary>
public sealed class AutoStartService
{
    public const string ValueName = "NMTools";
    public const string ShortcutFileName = "NMTools.lnk";
    public const string LauncherFileName = "Start-NMTools.vbs";
    public const string TrayArgument = "--tray";
    private const string DefaultLegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UiAutomationRunKeyRoot = @"Software\NMTools.UiAutomation";
    private const int MinimizedWindowStyle = 7;

    private readonly string _startupDirectory;
    private readonly string _legacyRunKeyPath;
    private readonly string? _launcherPath;
    private readonly string? _iconPath;

    public AutoStartService(string startupDirectory, string legacyRunKeyPath, string? launcherPath, string? iconPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRunKeyPath);
        _startupDirectory = startupDirectory;
        _legacyRunKeyPath = legacyRunKeyPath;
        _launcherPath = launcherPath;
        _iconPath = iconPath;
    }

    public bool IsAvailable => _launcherPath is not null && File.Exists(_launcherPath);

    public string ShortcutPath => Path.Combine(_startupDirectory, ShortcutFileName);

    public static AutoStartService CreateDefault()
    {
        var programDirectory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        var packageDirectory = programDirectory.Parent?.FullName ?? programDirectory.FullName;
        var launcherPath = Path.Combine(packageDirectory, LauncherFileName);

        // UI 自动化模式使用按测试目录隔离的启动文件夹和注册表项，不改动真实的开机启动项。
        if ((System.Windows.Application.Current as App)?.IsUiAutomationMode == true)
        {
            return new AutoStartService(
                Path.Combine(packageDirectory, "startup"),
                $@"{UiAutomationRunKeyRoot}\{programDirectory.Parent?.Name}\Run",
                launcherPath,
                Environment.ProcessPath);
        }

        return new AutoStartService(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            DefaultLegacyRunKeyPath,
            launcherPath,
            Environment.ProcessPath);
    }

    public static string BuildArguments(string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        return $"//B //Nologo \"{launcherPath}\" {TrayArgument}";
    }

    public static string WscriptPath => Path.Combine(Environment.SystemDirectory, "wscript.exe");

    public bool IsEnabled()
    {
        try
        {
            return File.Exists(ShortcutPath) || ReadLegacyValue() is not null;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException($"未找到 {LauncherFileName}，无法设置开机自启动。");
            }

            WriteShortcut();
            DeleteLegacyValue();
            return;
        }

        if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }

        DeleteLegacyValue();
    }

    /// <summary>
    /// 自启动已开启但记录的路径不是当前发布包时（例如整个文件夹被移动过）改写为当前路径，
    /// 并把旧版本的 Run 注册表项迁移为启动文件夹快捷方式。未开启或找不到启动器时不做任何事。
    /// </summary>
    public bool RefreshPathIfEnabled()
    {
        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            if (ReadLegacyValue() is not null)
            {
                WriteShortcut();
                DeleteLegacyValue();
                return true;
            }

            if (!File.Exists(ShortcutPath))
            {
                return false;
            }

            var current = ReadShortcut(ShortcutPath);
            if (current is not null
                && string.Equals(current.Value.Target, WscriptPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Value.Arguments, BuildArguments(_launcherPath!), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            WriteShortcut();
            return true;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException or IOException
                or COMException)
        {
            return false;
        }
    }

    public static (string Target, string Arguments)? ReadShortcut(string shortcutPath)
    {
        (string Target, string Arguments)? result = null;
        RunWithShell(shell =>
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                result = ((string)shortcut.TargetPath, (string)shortcut.Arguments);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        });
        return result;
    }

    private void WriteShortcut()
    {
        Directory.CreateDirectory(_startupDirectory);
        var launcherPath = _launcherPath!;
        var created = RunWithShell(shell =>
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            try
            {
                shortcut.TargetPath = WscriptPath;
                shortcut.Arguments = BuildArguments(launcherPath);
                shortcut.WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty;
                shortcut.WindowStyle = MinimizedWindowStyle;
                shortcut.Description = "NMTools";
                if (!string.IsNullOrEmpty(_iconPath) && File.Exists(_iconPath))
                {
                    shortcut.IconLocation = $"{_iconPath},0";
                }

                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        });

        if (!created)
        {
            throw new InvalidOperationException("无法创建启动文件夹快捷方式：系统缺少 WScript.Shell 组件。");
        }
    }

    private static bool RunWithShell(Action<dynamic> action)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return false;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            action(shell);
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private string? ReadLegacyValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_legacyRunKeyPath);
        return key?.GetValue(ValueName) is string value && value.Length > 0 ? value : null;
    }

    private void DeleteLegacyValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_legacyRunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
