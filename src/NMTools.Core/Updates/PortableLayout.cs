namespace NMTools.Core.Updates;

/// <summary>
/// 便携版目录布局与更新相关的路径规划。
///
/// 布局（见 ADR 0002）：
/// <code>
/// NMTools-win-x64/
/// ├── Start-NMTools.vbs
/// ├── README.txt
/// └── program/
///     ├── NMTools.App.exe
///     ├── NMTools.Updater.exe
///     ├── .runtime/      运行时缓存，可重建
///     ├── data/          用户设置，更新必须保留
///     └── logs/          诊断日志，更新时保留
/// </code>
///
/// 更新期间额外出现（都在包根，与 <c>program/</c> 同级）：
/// <code>
/// .update-staging/    下载的 zip 与解压结果，新版本启动确认后清理
/// program.old/        旧版本备份，新版本启动确认后清理
/// .update-verify      更新刚完成的标记，主程序启动成功后据此清理上面两项
/// </code>
/// </summary>
public static class PortableLayout
{
    public const string AppDirectoryName = "program";

    public const string AppExecutableName = "NMTools.App.exe";

    public const string UpdaterExecutableName = "NMTools.Updater.exe";

    public const string LauncherFileName = "Start-NMTools.vbs";

    public const string DataDirectoryName = "data";

    public const string LogsDirectoryName = "logs";

    public const string RuntimeDirectoryName = ".runtime";

    /// <summary>
    /// 更新暂存目录，位于发布包根目录（与 <c>program/</c> 同级），**不在** <c>program/</c> 内。
    ///
    /// 这里的关键约束：更新器要从暂存目录里运行，而它随后要把整个 <c>program/</c> 改名成
    /// <c>program.old/</c>。Windows 不允许重命名含有正在运行程序的目录，如果暂存目录在
    /// <c>program/</c> 内部，改名必然失败。放在同级就能避开这个问题。
    /// </summary>
    public const string StagingDirectoryName = ".update-staging";

    /// <summary>旧版本备份目录，与 <c>program/</c> 同级。</summary>
    public const string BackupDirectoryName = "program.old";

    /// <summary>
    /// 「下次启动时更新」的待办标记，位于包根。主程序写入，下次启动由更新器消费。
    /// </summary>
    public const string PendingUpdateMarkerName = ".update-pending";

    /// <summary>
    /// 「更新已完成，等待确认」标记，位于包根。更新器替换成功后写入，
    /// 主程序启动成功后据此判定新版本可用，然后清理 <c>program.old/</c> 与暂存目录并删除本标记。
    ///
    /// 标记残留而程序没能正常启动时，说明新版本有问题，需要人工回滚。
    /// </summary>
    public const string VerifyUpdateMarkerName = ".update-verify";

    /// <summary>程序目录（<c>program/</c>）的完整路径。</summary>
    public static string ResolveAppDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.Combine(baseDirectory, AppDirectoryName);
    }

    /// <summary>发布包根目录（含 <c>Start-NMTools.vbs</c> 的那一层）。</summary>
    public static string ResolvePackageRoot(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        // 保留调用方给的路径形态（相对就取相对的上级），便于测试和调用处对照。
        var trimmed = appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        return parent ?? throw new InvalidOperationException("程序目录没有上级目录。");
    }

    /// <summary>
    /// 更新暂存目录，位于发布包根目录下，与 <c>program/</c> 同级。
    /// 更新器从这里运行，因此它不能落在被替换的 <c>program/</c> 之内。
    /// </summary>
    public static string ResolveStagingDirectory(string appDirectory) =>
        Path.Combine(ResolvePackageRoot(appDirectory), StagingDirectoryName);

    public static string ResolveBackupDirectory(string appDirectory) =>
        Path.Combine(ResolvePackageRoot(appDirectory), BackupDirectoryName);

    public static string ResolvePendingUpdateMarker(string appDirectory) =>
        Path.Combine(ResolvePackageRoot(appDirectory), PendingUpdateMarkerName);

    public static string ResolveVerifyUpdateMarker(string appDirectory) =>
        Path.Combine(ResolvePackageRoot(appDirectory), VerifyUpdateMarkerName);

    /// <summary>
    /// 判断某个相对路径是否属于必须保留的用户数据。
    /// 更新替换 <c>program/</c> 时，这些路径要么不删，要么在删除前搬走。
    /// </summary>
    public static bool IsPreservedPath(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        if (normalized is null)
        {
            return false;
        }

        return IsSameOrUnder(normalized, DataDirectoryName)
            || IsSameOrUnder(normalized, LogsDirectoryName);
    }

    /// <summary>
    /// 判断某个相对路径是否可以安全地从新版本中替换进来。
    /// 暂存目录和备份目录本身不能出现在更新内容里。
    /// </summary>
    public static bool IsReplaceablePath(string relativePath)
    {
        // 拒绝绝对路径与盘符，拒绝向上跳级。
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = NormalizeRelative(relativePath);
        if (normalized is null)
        {
            return false;
        }

        // 遇到 ".." 段直接拒绝，而不是只比较前缀。
        if (relativePath.Replace('\\', '/').Split('/').Any(segment => segment.Trim() == ".."))
        {
            return false;
        }

        // 暂存和备份目录不能被更新内容覆盖。
        return !IsSameOrUnder(normalized, StagingDirectoryName)
            && !IsSameOrUnder(normalized, BackupDirectoryName);
    }

    /// <summary>
    /// 把相对路径规整为 <c>a/b/c</c> 形式，去掉 <c>./</c> 与首尾分隔符。含 <c>..</c> 时返回 null。
    /// </summary>
    private static string? NormalizeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var raw in relativePath.Replace('\\', '/').Split('/'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>
    /// <paramref name="normalized"/> 是否等于 <paramref name="directory"/> 或位于其下。
    /// 用段边界判断，避免 <c>database/</c> 被误判成 <c>data/</c> 的子路径。
    /// </summary>
    private static bool IsSameOrUnder(string normalized, string directory) =>
        normalized.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把一个归档条目名解析成相对目标目录的安全路径。不安全时返回 null。
    /// </summary>
    public static string? ResolveSafeEntryPath(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return null;
        }

        var normalized = entryName.Replace('\\', '/');

        // 绝对路径、UNC、盘符一律拒绝。
        if (normalized.StartsWith('/') || normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            return null;
        }

        // 路径穿越在任何位置都不允许。
        var segments = new List<string>();
        foreach (var raw in normalized.Split('/'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return null;
            }

            // Windows 不允许的字符与保留设备名。
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }

            if (IsReservedWindowsName(segment))
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4
                && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && char.IsAsciiDigit(name[3]));
    }
}
