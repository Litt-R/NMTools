using System.Diagnostics;
using System.IO;
using NMTools.Core.Updates;

namespace NMTools.Updater;

/// <summary>
/// 执行一次替换：等待主程序退出 → 备份旧目录 → 写入新版本 → 恢复用户数据。
///
/// 设计约束（见 ROADMAP 第 4 阶段）：
/// - 先改名为备份目录，而不是先删除。改名是原子的，任何时刻磁盘上都有一份完整可运行的旧版本；
/// - 用户数据（<c>data/</c>、<c>logs/</c>）从备份目录复制回来，绝不因为"新包里没有"而丢失；
/// - 失败时尝试把备份目录改名回 <c>program/</c>，恢复旧版本；
/// - 不清理 <c>program/</c> 之外的任何东西。
/// </summary>
public sealed class UpdateApplier
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(30);

    private readonly Action<string> _log;

    public UpdateApplier(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// 应用更新。
    /// </summary>
    /// <param name="pid">需要等待退出的主程序进程 ID。</param>
    /// <param name="appDirectory">当前 <c>program/</c> 目录。</param>
    /// <param name="extractedRoot">解压后的新版本根目录（内含 <c>program/</c> 或直接是 program 内容）。</param>
    public ApplyResult Apply(int pid, string appDirectory, string extractedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedRoot);

        if (!WaitForProcessExit(pid))
        {
            // 主程序没有退出，绝对不能动它的文件。
            _log($"process {pid} did not exit within {ProcessExitTimeout.TotalSeconds}s");
            return ApplyResult.Failure(ApplyFailureReason.ProcessStillRunning);
        }

        var newProgramSource = ResolveNewProgramDirectory(extractedRoot);
        if (newProgramSource is null)
        {
            _log($"no program directory found under {extractedRoot}");
            return ApplyResult.Failure(ApplyFailureReason.MissingNewProgram);
        }

        var backupDirectory = PortableLayout.ResolveBackupDirectory(appDirectory);
        var appParent = Path.GetDirectoryName(Path.GetFullPath(appDirectory))!;

        // 更新器必须运行在 program/ 之外。它要重命名整个 program/，而 Windows 不允许
        // 重命名含有正在运行可执行文件的目录。这里先自查，给出准确日志，
        // 而不是让 Directory.Move 抛一个语焉不详的 IOException。
        if (IsRunningInside(appDirectory))
        {
            _log($"updater is running inside {appDirectory}; cannot rename it");
            return ApplyResult.Failure(ApplyFailureReason.UpdaterInsideTarget);
        }

        if (!IsWritable(appParent))
        {
            _log($"target directory is not writable: {appParent}");
            return ApplyResult.Failure(ApplyFailureReason.TargetNotWritable);
        }

        // 上一次更新留下的备份目录不应存在；存在说明上次没清理干净，先拒绝执行避免覆盖唯一一份可运行版本。
        if (Directory.Exists(backupDirectory))
        {
            _log($"stale backup directory exists: {backupDirectory}");
            return ApplyResult.Failure(ApplyFailureReason.StaleBackup);
        }

        // 0. 先删运行时解压缓存。它是可重建的，而且正是最容易卡住改名的东西：
        //    单文件 exe 首次运行会把几十 MB 原生 DLL 解压到 .runtime/，杀毒软件会在
        //    后台扫描这些新文件并持有句柄，导致整个 program/ 无法改名。
        //    删掉它，改名就能顺利进行；新版本首次启动会自己重新解压。
        TryDeleteRuntimeCache(appDirectory);

        // 1. 先复制旧目录做备份（而不是改名）。
        //
        // 最初用 Directory.Move 做原子改名，但在真实环境里长期失败：即使主程序已退出，
        // 目录仍被某个句柄占着（杀软扫描、或 Windows 自身的延迟释放），改不了名。
        // 复制虽然慢一些，但只依赖文件读取——实测更可靠，且同样保留完整旧版本。
        if (!TryCopyDirectoryWithRetry(appDirectory, backupDirectory, out var failureType))
        {
            _log($"backup copy failed after retries: {failureType}");
            CleanupPartialDirectory(backupDirectory);
            return ApplyResult.Failure(ApplyFailureReason.BackupFailed);
        }

        _log($"backed up {appDirectory} -> {backupDirectory}");

        try
        {
            // 2. 就地替换：清掉 program/ 里除用户数据之外的内容，再写入新版本。
            //    不删除 program/ 本身——目录被锁时删不掉，而里面的文件是可以逐个替换的。
            ClearReplaceableContent(appDirectory);
            CopyDirectory(newProgramSource, appDirectory);
            _log("new version applied");
            return ApplyResult.Success(backupDirectory);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            _log($"apply failed: {exception.GetType().Name}: {exception.Message}");
            TryRollback(appDirectory, backupDirectory);
            return ApplyResult.Failure(ApplyFailureReason.ApplyFailed);
        }
    }

    /// <summary>
    /// 清掉 <c>program/</c> 下除用户数据之外的所有条目。
    ///
    /// 只删内容、不删目录本身，是为了绕开"目录被句柄占用时无法删除/改名"的问题。
    /// 删不掉的文件记日志但不中止：后续复制会覆盖同名文件，
    /// 剩下的旧文件留在原处不影响新版本运行。
    /// </summary>
    private void ClearReplaceableContent(string appDirectory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(appDirectory))
        {
            var name = Path.GetFileName(entry);
            if (PortableLayout.IsPreservedPath(name))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 删不掉就让后续复制覆盖它。
                _log($"could not remove {name}: {exception.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// 带重试的目录复制。先复制再替换的方案不依赖目录改名，
    /// 因此不会因为目录句柄被持有而整体失败。
    /// </summary>
    private bool TryCopyDirectoryWithRetry(string source, string destination, out string? failureType)
    {
        var delays = new[] { 100, 200, 400 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                CopyDirectory(source, destination);
                failureType = null;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= delays.Length)
                {
                    failureType = $"{exception.GetType().Name}: {exception.Message}";
                    return false;
                }

                _log($"backup copy attempt {attempt + 1} failed ({exception.GetType().Name}), retrying");
                CleanupPartialDirectory(destination);
                Thread.Sleep(delays[attempt]);
            }
        }
    }

    private static void CleanupPartialDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 回滚：用备份内容覆盖 <c>program/</c>。
    ///
    /// 同样不依赖目录改名——与备份阶段用同一套机制，避免在出问题的环境下
    /// 回滚本身也失败。备份目录保持不动，回滚后仍可由清理流程处理。
    /// </summary>
    public bool Rollback(string appDirectory, string backupDirectory)
    {
        try
        {
            if (!Directory.Exists(backupDirectory))
            {
                _log("rollback skipped: backup directory is missing");
                return false;
            }

            ClearReplaceableContent(appDirectory);
            CopyDirectory(backupDirectory, appDirectory, skipDirectories: [PortableLayout.DataDirectoryName, PortableLayout.LogsDirectoryName]);

            _log("rolled back to previous version");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"rollback failed: {exception.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// 删除 <c>.runtime/</c> 运行时解压缓存。
    ///
    /// 它是单文件 exe 首次运行解压出来的原生 DLL，删掉后新版本会自行重建，
    /// 不属于用户数据。更关键的是：这些文件刚被写入磁盘，杀毒软件往往正在扫描，
    /// 持有句柄会让整个 <c>program/</c> 无法重命名——这是更新失败最常见的原因。
    /// 删不掉也不中止流程，交给后面的重试。
    /// </summary>
    private void TryDeleteRuntimeCache(string appDirectory)
    {
        var runtime = Path.Combine(appDirectory, PortableLayout.RuntimeDirectoryName);
        if (!Directory.Exists(runtime))
        {
            return;
        }

        try
        {
            Directory.Delete(runtime, recursive: true);
            _log("deleted .runtime cache before replacing");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删不掉就继续走改名流程，由重试兜底。
            _log($"runtime cache delete failed: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// 带重试的目录改名。进程退出后 Windows 可能仍短暂持有文件句柄，
    /// 直接 Move 会失败；退避重试能覆盖这段窗口而不必让用户重来。
    /// </summary>
    private bool TryMoveDirectoryWithRetry(string source, string destination, out string? failureType)
    {
        // 间隔递增，总等待约 3 秒。正常情况下第一次就能成功，只有句柄释放慢时才用得上。
        var delays = new[] { 100, 200, 400, 800, 1500 };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                failureType = null;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= delays.Length)
                {
                    failureType = exception.GetType().Name;
                    return false;
                }

                // 只记类型不够——IOException 的 Message 会包含具体原因（被占用、路径问题等）。
                // 这里不含任何用户数据，可以安全落日志。
                _log($"backup move attempt {attempt + 1} failed ({exception.GetType().Name}): {exception.Message}");
                Thread.Sleep(delays[attempt]);
            }
        }
    }

    private bool WaitForProcessExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return true;
            }

            return process.WaitForExit(ProcessExitTimeout);
        }
        catch (ArgumentException)
        {
            // 进程已经不存在了，视为已退出。
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// 解压结果可能是「包根目录（含 program/）」或「program/ 本身」，两种都接受。
    /// </summary>
    /// <summary>
    /// 找出解压结果里的 <c>program/</c> 目录。
    ///
    /// 发布包在 zip 里套了一层版本目录，解压根可能是「包根（含 program/）」，
    /// 也可能因为调用方传错层级而差一层。这里同时接受两种，并向下最多找一层，
    /// 避免因为路径层级不一致就判定"包不完整"。
    /// </summary>
    private static string? ResolveNewProgramDirectory(string extractedRoot)
    {
        var direct = TryResolveAt(extractedRoot);
        if (direct is not null)
        {
            return direct;
        }

        // 差一层时（例如传进来的是 staging/content 而实际包根在它的子目录里）向下找。
        try
        {
            foreach (var child in Directory.EnumerateDirectories(extractedRoot))
            {
                var resolved = TryResolveAt(child);
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? TryResolveAt(string root)
    {
        var nested = Path.Combine(root, PortableLayout.AppDirectoryName);
        if (File.Exists(Path.Combine(nested, PortableLayout.AppExecutableName)))
        {
            return nested;
        }

        // 解压结果本身就是 program/ 的内容。
        return File.Exists(Path.Combine(root, PortableLayout.AppExecutableName)) ? root : null;
    }


    private void TryRollback(string appDirectory, string backupDirectory)
    {
        if (Directory.Exists(backupDirectory))
        {
            Rollback(appDirectory, backupDirectory);
        }
    }

    /// <param name="skipDirectories">
    /// 复制时跳过的顶层子目录名。回滚时用它保留现有的 <c>data/</c> 与 <c>logs/</c>，
    /// 避免用备份里可能过时的设置覆盖用户当前数据。
    /// </param>
    private static void CopyDirectory(
        string source,
        string destination,
        IReadOnlyCollection<string>? skipDirectories = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (skipDirectories is not null
                && skipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(destination, name));
        }
    }

    /// <summary>
    /// 更新器自己的可执行文件是否位于 <paramref name="appDirectory"/> 之内。
    /// 在就说明暂存目录放错了位置，重命名必然失败。
    /// </summary>
    private static bool IsRunningInside(string appDirectory)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
            {
                return false;
            }

            var target = Path.GetFullPath(appDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var selfPath = Path.GetFullPath(self);

            return selfPath.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".nm-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public enum ApplyFailureReason
{
    None,
    ProcessStillRunning,
    MissingNewProgram,
    TargetNotWritable,
    StaleBackup,
    BackupFailed,
    ApplyFailed,

    /// <summary>更新器自身位于待替换目录内，无法重命名该目录。属于部署错误，不应发生。</summary>
    UpdaterInsideTarget,
}

/// <param name="BackupDirectory">成功时的旧版本备份目录，供主程序启动确认后清理。</param>
public sealed record ApplyResult(
    bool IsSuccess,
    ApplyFailureReason FailureReason,
    string? BackupDirectory)
{
    public static ApplyResult Success(string backupDirectory) =>
        new(true, ApplyFailureReason.None, backupDirectory);

    public static ApplyResult Failure(ApplyFailureReason reason) =>
        new(false, reason, null);
}
