using System.IO;
using System.Windows;
using NMTools.App.Infrastructure;

namespace NMTools.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstanceService;
    private TrayIconService? _trayIconService;
    private MainWindow? _mainWindow;
    private DiagnosticLog? _diagnosticLog;

    internal IDiagnosticLog? Logger => _diagnosticLog;

    internal bool IsUiAutomationMode { get; private set; }

    private bool IsUiAutomationSettingsMode { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        IsUiAutomationMode = string.Equals(
                Environment.GetEnvironmentVariable("NMTOOLS_UI_AUTOMATION"),
                "1",
                StringComparison.Ordinal)
            && IsRunningFromUiAutomationDirectory();
        IsUiAutomationSettingsMode = IsUiAutomationMode
            && e.Args.Contains("--ui-test-settings", StringComparer.OrdinalIgnoreCase);

        if (!IsUiAutomationMode)
        {
            _singleInstanceService = SingleInstanceService.TryCreate();
            if (_singleInstanceService is null)
            {
                Shutdown();
                return;
            }

            AutoStartService.CreateDefault().RefreshPathIfEnabled();
            _singleInstanceService.ShowMainWindowRequested += OnShowMainWindowRequested;
            _trayIconService = new TrayIconService(
                OnRequestShow,
                OnRequestSettings,
                OnRequestOpenLogs,
                OnRequestExit);
        }

        // UI 自动化测试不能往用户的真实日志目录里写诊断内容。
        _diagnosticLog = IsUiAutomationMode ? null : DiagnosticLog.CreateForAppDirectory();
        _diagnosticLog?.Info(
            "app.start",
            $"version={typeof(App).Assembly.GetName().Version} session={System.Diagnostics.Process.GetCurrentProcess().SessionId} "
            + $"os={Environment.OSVersion.VersionString} is64BitProcess={Environment.Is64BitProcess}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _mainWindow = new MainWindow(_diagnosticLog);
        if (_trayIconService is not null)
        {
            _trayIconService.UpdateToolTip(_mainWindow.CurrentOptions);
            _mainWindow.OptionsChanged += (_, updated) => _trayIconService.UpdateToolTip(updated);
        }

        // UI 自动化使用独立配置目录，并禁用托盘和全局热键，不触碰用户正在运行的实例。
        // 普通启动则在显示主窗口时注册全局热键。
        if (IsUiAutomationSettingsMode)
        {
            _mainWindow.ShowSettings();
            Shutdown();
            return;
        }

        if (!IsUiAutomationMode && e.Args.Contains(AutoStartService.TrayArgument, StringComparer.OrdinalIgnoreCase))
        {
            // PrepareHidden 只创建窗口句柄（用于注册全局热键），主窗口保持不可见，只驻留托盘。
            _mainWindow.PrepareHidden();
            // 窗口不显示就不会触发 Loaded，所以自动检查必须在这里单独发起；
            // 否则开机自启动的用户永远等不到检查。检查本身异步且失败静默，不会让窗口冒出来。
            _mainWindow.StartStartupUpdateCheckIfEnabled();
            return;
        }

        _mainWindow.Show();
        _mainWindow.StartStartupUpdateCheckIfEnabled();
    }

    private static bool IsRunningFromUiAutomationDirectory()
    {
        var directory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        var runDirectory = directory.Parent;
        var automationDirectory = runDirectory?.Parent;
        var tempRoot = Path.GetFullPath(Path.GetTempPath());

        return directory.Name.Equals("program", StringComparison.OrdinalIgnoreCase)
            && runDirectory is not null
            && automationDirectory is not null
            && automationDirectory.Name.Equals("NMTools.UiAutomation", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(runDirectory.Name, out _)
            && automationDirectory.FullName.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceService is not null)
        {
            _singleInstanceService.ShowMainWindowRequested -= OnShowMainWindowRequested;
        }

        _mainWindow?.DisposeResources();
        _trayIconService?.Dispose();
        _singleInstanceService?.Dispose();
        // 退出前把队列里的记录落盘，否则最后几行关键信息会随进程一起消失。
        _diagnosticLog?.Info("app.exit", "shutdown requested");
        _diagnosticLog?.Dispose();
        _diagnosticLog = null;
        base.OnExit(e);
    }

    private void OnShowMainWindowRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => _mainWindow?.ShowFromTray()));
    }

    private void OnDispatcherUnhandledException(
        object? sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 托盘菜单和设置入口不得因 UI 异常直接结束进程；记录最小诊断信息并提示用户。
        _diagnosticLog?.Error("ui.unhandledException", "dispatcher unhandled exception", e.Exception);
        try
        {
            var diagnosticDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(diagnosticDirectory);
            var diagnosticPath = Path.Combine(diagnosticDirectory, "last-error.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath)!);
            File.WriteAllText(
                diagnosticPath,
                $"{DateTimeOffset.Now:O}\n{e.Exception}\n");
        }
        catch
        {
            // 诊断写入失败不应掩盖原始 UI 异常。
        }

        System.Windows.MessageBox.Show(
            $"NMTools 遇到界面错误：{e.Exception.Message}",
            "NMTools - 界面错误",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnRequestShow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowFromTray();
    }

    private void OnRequestSettings()
    {
        _mainWindow?.ShowSettings();
    }

    /// <summary>
    /// 打开日志目录供用户直接取走日志文件反馈问题。目录不存在时先创建，
    /// 创建失败则提示具体路径，避免"点了没反应"式的静默失败。
    /// </summary>
    private void OnRequestOpenLogs()
    {
        var directory = DiagnosticLog.ResolveLogDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            _diagnosticLog?.Info("logs.opened", "user opened the log directory");
        }
        catch (Exception exception)
        {
            _diagnosticLog?.Error("logs.openFailed", $"directory={directory}", exception);
            System.Windows.MessageBox.Show(
                $"无法打开日志文件夹：{directory}\n{exception.Message}",
                "NMTools - 打开日志失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnRequestExit()
    {
        Shutdown();
    }
}
