using System.IO;
using System.Windows;
using NMTools.App.Infrastructure;
using NMTools.App.Services;
using NMTools.Core.Updates;

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

        // UI 自动化测试不能往用户的真实日志目录里写诊断内容。
        // 日志要早于排期更新初始化：升级流程本身也需要留下记录。
        _diagnosticLog = IsUiAutomationMode ? null : DiagnosticLog.CreateForAppDirectory();
        _diagnosticLog?.Info(
            "app.start",
            $"version={typeof(App).Assembly.GetName().Version} session={System.Diagnostics.Process.GetCurrentProcess().SessionId} "
            + $"os={Environment.OSVersion.VersionString} is64BitProcess={Environment.Is64BitProcess}");

        if (!IsUiAutomationMode)
        {
            // 必须在单实例检查和热键注册之前处理排期更新：如果本次启动要升级，
            // 我们马上就会退出，没必要先占用互斥体和全局热键。
            if (TryApplyScheduledUpdate())
            {
                Shutdown();
                return;
            }

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

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // 走到这里说明本次启动没有被排期更新接管，启动本身是成功的。
        // 清掉可能残留的待办标记：更新若已由更新器完成，这里就是收尾；
        // 若标记还在但暂存目录已被清理，也在此一并清掉，避免下次启动反复尝试。
        PendingUpdateStore.Clear(appDirectory);

        // 如果刚被更新器替换过，现在能启动到这里就证明新版本可用，
        // 可以安全删除旧版本备份与暂存目录了。没有标记时该调用立即返回，无副作用。
        if (!IsUiAutomationMode)
        {
            var outcome = UpdateCleanup.RunIfUpdateCompleted(
                appDirectory,
                message => _diagnosticLog?.Info("update.cleanup", message));
            _diagnosticLog?.Info("update.cleanup.outcome", $"result={outcome}");
        }

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

    /// <summary>
    /// 如果上次选择了「下次启动时更新」，在启动早期把控制权交给更新器并返回 true，
    /// 调用方应立刻退出。
    ///
    /// 待办里记录了更新包的解压目录；只要它还在就直接应用。目录已被清理时清掉待办，
    /// 按普通启动继续——宁可这次不升级，也不能让程序无法启动。
    /// </summary>
    private bool TryApplyScheduledUpdate()
    {
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        PendingUpdate? pending;
        try
        {
            pending = PendingUpdateStore.Load(appDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            PendingUpdateStore.Clear(appDirectory);
            return false;
        }

        if (pending is null)
        {
            return false;
        }

        if (!Directory.Exists(pending.ContentDirectory))
        {
            // 暂存目录没了（用户清理过或磁盘被清），排期失效。
            _diagnosticLog?.Warn("update.scheduled.missingStaging", "staged content is gone, clearing marker");
            PendingUpdateStore.Clear(appDirectory);
            return false;
        }

        var installer = new UpdateInstaller(_diagnosticLog);
        var updaterPid = installer.LaunchUpdater(appDirectory, pending.ContentDirectory);
        installer.Dispose();

        if (updaterPid is null)
        {
            _diagnosticLog?.Warn("update.scheduled.updaterStartFailed", "could not start updater");
            PendingUpdateStore.Clear(appDirectory);
            return false;
        }

        _diagnosticLog?.Info("update.scheduled.applying", $"version={pending.Version} pid={updaterPid}");
        return true;
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
