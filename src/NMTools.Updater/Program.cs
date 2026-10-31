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

        // 日志放在 program/logs/，与主程序诊断日志同处，排查时只需要看一个目录。
        // 主程序退出后目录仍可写；写不进去时 UpdaterLog 静默降级。
        var logger = new UpdaterLog(UpdaterLog.ResolvePath(options.AppDirectory, LogFileName));
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
                    ApplyFailureReason.UpdaterInsideTarget =>
                        "更新程序的位置不正确，无法替换文件。请重新下载完整发布包。",
                    _ =>
                        "更新失败，已尝试恢复原版本。若程序无法启动，请重新下载完整发布包。",
                });
                return 1;
            }

            // 写下"更新已完成"标记。主程序下次启动成功后据此清理 program.old/ 与暂存目录；
            // 标记还在却没能正常启动时，备份会保留下来供回滚。
            // 注意清理不在这里做：此刻新版本还没验证过能否启动，删掉备份就失去回滚机会了。
            var newVersion = ReadVersion(options.AppDirectory);
            UpdateCleanup.WriteVerifyMarker(options.AppDirectory, newVersion, logger.Write);

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

    /// <summary>
    /// 读取新版本 exe 的文件版本，仅用于写进标记便于事后核对。
    /// 读不到时返回 0.0.0.0 —— 标记的作用只是"有一次更新刚完成"。
    /// </summary>
    private static Version ReadVersion(string appDirectory)
    {
        try
        {
            var exe = Path.Combine(appDirectory, PortableLayout.AppExecutableName);
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return Version.TryParse(info.FileVersion, out var parsed)
                ? parsed
                : new Version(0, 0, 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Version(0, 0, 0, 0);
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
public sealed record UpdaterOptions(
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

        // 统一去掉尾部反斜杠。调用方若传了 "D:\...\program\"，反斜杠会与收尾引号
        // 混淆，副作用是这里的值带着多余分隔符，后续 Path.Combine 会拼出双斜杠。
        var normalizedApp = appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedStaged = stagedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 未提供时按约定推导：暂存根就是解压目录的上一级。
        stagingRoot ??= Path.GetDirectoryName(normalizedStaged);

        return new UpdaterOptions(
            pid.Value,
            normalizedApp,
            normalizedStaged,
            stagingRoot ?? string.Empty);
    }
}

internal sealed class UpdaterLog : IDisposable
{
    private readonly StreamWriter? _writer;

    /// <summary>
    /// 解析日志路径：<c>program/logs/updater.log</c>，与主程序诊断日志同目录。
    /// 目录不可写时由构造函数静默降级。
    /// </summary>
    public static string ResolvePath(string appDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.Combine(appDirectory, PortableLayout.LogsDirectoryName, fileName);
    }

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
