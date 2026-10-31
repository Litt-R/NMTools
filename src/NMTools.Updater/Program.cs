using System.IO;
using System.Runtime.InteropServices;
using NMTools.Core.Updates;

namespace NMTools.Updater;

internal static class Program
{
    private const string LogFileName = "updater.log";

    [STAThread]
    private static int Main(string[] args)
    {
        // 更新器不需要常驻，出错时也不要静默消失：写日志并给一个可见提示，
        // 因为此刻主程序已经退出，用户看到的是一个"什么都没发生"的桌面。
        var options = UpdaterOptions.Parse(args);
        if (options is null)
        {
            ShowError("更新参数无效，已取消更新。请重新启动 NMTools 后重试。");
            return 2;
        }

        var logDirectory = Path.GetDirectoryName(options.AppDirectory) ?? AppContext.BaseDirectory;
        var logger = new UpdaterLog(Path.Combine(logDirectory, LogFileName));
        logger.Write($"start pid={options.ProcessId} app={options.AppDirectory} staging={options.StagedDirectory}");

        try
        {
            var applier = new UpdateApplier(logger.Write);
            var result = applier.Apply(options.ProcessId, options.AppDirectory, options.StagedDirectory);
            logger.Write($"result success={result.IsSuccess} reason={result.FailureReason}");

            if (!result.IsSuccess)
            {
                ShowError(result.FailureReason switch
                {
                    ApplyFailureReason.ProcessStillRunning =>
                        "NMTools 仍在运行，更新已取消。请完全退出后再试。",
                    ApplyFailureReason.TargetNotWritable =>
                        "程序目录不可写，无法更新。请把 NMTools 移到你有写权限的位置，或以当前用户身份重新解压。",
                    ApplyFailureReason.StaleBackup =>
                        "发现上次更新遗留的备份目录 program.old，已取消更新以免丢失可运行版本。请手动确认后删除该目录再试。",
                    ApplyFailureReason.MissingNewProgram =>
                        "下载的更新包不完整，已保留当前版本。",
                    _ =>
                        "更新失败，已尝试恢复原版本。若程序无法启动，请重新下载完整发布包。",
                });
                return 1;
            }

            // 更新成功后清掉暂存目录，避免下次误用。
            // 更新器自身正从暂存目录里运行，无法删除自己的 EXE，所以只清内容，
            // 自己的副本留给下次更新覆盖（它位于 .update-staging/ 内，不污染 program/）。
            TryDeleteDirectory(options.StagedDirectory, logger.Write);

            // 重新拉起程序，让用户看到的就是升级后的界面。启动入口优先用 VBS（无控制台窗口）。
            LaunchApp(options.AppDirectory, logger.Write);
            return 0;
        }
        catch (Exception exception)
        {
            logger.Write($"unhandled {exception.GetType().Name}: {exception.Message}");
            ShowError("更新过程中出现错误，已尝试恢复原版本。");
            return 1;
        }
        finally
        {
            logger.Dispose();
        }
    }

    private static void LaunchApp(string appDirectory, Action<string> log)
    {
        var packageRoot = PortableLayout.ResolvePackageRoot(appDirectory);
        var launcher = Path.Combine(packageRoot, PortableLayout.LauncherFileName);
        var exe = Path.Combine(appDirectory, PortableLayout.AppExecutableName);

        try
        {
            if (File.Exists(launcher))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wscript.exe",
                    Arguments = $"//B //Nologo \"{launcher}\"",
                    UseShellExecute = false,
                    WorkingDirectory = packageRoot,
                });
                log("relaunched via launcher");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = appDirectory,
            });
            log("relaunched via exe");
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"relaunch failed: {exception.GetType().Name}");
        }
    }

    private static void TryDeleteDirectory(string directory, Action<string> log)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响更新结果，下次更新会覆盖。
            log($"staging cleanup failed: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// 用 Win32 MessageBox 而不是 WinForms：更新器只需要一个提示框，
    /// 引入 WinForms 会带来整个框架依赖，也让发布包无法裁剪。
    /// </summary>
    private static void ShowError(string message)
    {
        NativeMethods.MessageBoxW(IntPtr.Zero, message, "NMTools - 更新失败", MbIconWarning);
    }

    private const uint MbIconWarning = 0x00000030;

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}

/// <summary>
/// 更新器的命令行参数。由主程序在退出前传入，不读取任何配置文件，
/// 因此不会因为用户改设置而影响更新行为。
/// </summary>
internal sealed record UpdaterOptions(
    int ProcessId,
    string AppDirectory,
    string StagedDirectory,
    string StagingRoot)
{
    public static UpdaterOptions? Parse(string[] args)
    {
        int? pid = null;
        string? appDirectory = null;
        string? stagedDirectory = null;
        string? stagingRoot = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                return null;
            }

            switch (args[i])
            {
                case "--pid":
                    if (!int.TryParse(args[++i], out var parsed) || parsed <= 0)
                    {
                        return null;
                    }

                    pid = parsed;
                    break;
                case "--app-dir":
                    appDirectory = args[++i];
                    break;
                case "--staged-dir":
                    stagedDirectory = args[++i];
                    break;
                case "--staging-root":
                    stagingRoot = args[++i];
                    break;
                default:
                    return null;
            }
        }

        if (pid is null
            || string.IsNullOrWhiteSpace(appDirectory)
            || string.IsNullOrWhiteSpace(stagedDirectory))
        {
            return null;
        }

        // 未提供时按约定推导：暂存根就是解压目录的上一级。
        stagingRoot ??= Path.GetDirectoryName(stagedDirectory.TrimEnd(Path.DirectorySeparatorChar));

        return new UpdaterOptions(
            pid.Value,
            appDirectory,
            stagedDirectory,
            stagingRoot ?? string.Empty);
    }
}

internal sealed class UpdaterLog : IDisposable
{
    private readonly StreamWriter? _writer;

    public UpdaterLog(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    public void Write(string message)
    {
        try
        {
            _writer?.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // 更新日志不是核心功能，写失败不能中断更新。
        }
    }

    public void Dispose() => _writer?.Dispose();
}
