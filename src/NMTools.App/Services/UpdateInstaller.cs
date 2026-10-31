using System.IO;
using System.Net.Http;
using System.Text.Json;
using NMTools.App.Infrastructure;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 把「下载 → 校验 → 解压 → 暂存」串起来，并记录"下次启动时应用"的待办。
///
/// 真正替换文件由独立进程 <c>NMTools.Updater.exe</c> 完成（见 ADR 0005），
/// 因为主程序无法替换正在运行的自身。
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    private readonly UpdatePackageDownloader _downloader;
    private readonly UpdatePackageExtractor _extractor;
    private readonly IDiagnosticLog? _log;

    public UpdateInstaller(IDiagnosticLog? log = null, HttpMessageHandler? handler = null)
    {
        _log = log;
        _downloader = new UpdatePackageDownloader(log, handler);
        _extractor = new UpdatePackageExtractor(message => log?.Info("update.extract", message));
    }

    /// <summary>
    /// 下载并解压更新包到暂存目录。
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(
        UpdateManifest manifest,
        string appDirectory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        if (!manifest.SupportsAutomaticUpdate)
        {
            // 没有匹配的便携包资产，调用方应退回打开浏览器。
            return PrepareResult.Failure(PrepareFailureReason.NotSupported);
        }

        var stagingDirectory = PortableLayout.ResolveStagingDirectory(appDirectory);

        // 下载。大小在下载器内部对照清单声明值校验。
        var download = await _downloader
            .DownloadAsync(manifest, stagingDirectory, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!download.IsSuccess)
        {
            return PrepareResult.Failure(MapDownloadFailure(download.FailureReason));
        }

        // 解压。zip 结构损坏、条目越界、缺少主程序都会在这一步被发现；
        // 目录结构为 <staging>/content。
        var contentDirectory = Path.Combine(stagingDirectory, "content");
        var extract = _extractor.Extract(download.ArchivePath!, contentDirectory);
        if (!extract.IsSuccess)
        {
            TryDeleteDirectory(stagingDirectory);
            return PrepareResult.Failure(MapExtractFailure(extract.FailureReason));
        }

        TryDeleteFile(download.ArchivePath!);
        _log?.Info("update.prepare.ready", $"version={manifest.Version}");
        return PrepareResult.Success(contentDirectory, manifest.Version);
    }

    /// <summary>
    /// 写下"下次启动时应用"的待办，并把更新的启动器与暂存位置记进去。
    /// </summary>
    public bool ScheduleForNextStartup(string appDirectory, string contentDirectory, Version version)
    {
        var pending = new PendingUpdate
        {
            Version = version.ToString(),
            ContentDirectory = contentDirectory,
            AppDirectory = appDirectory,
        };

        return PendingUpdateStore.Save(
            PortableLayout.ResolvePendingUpdateMarker(appDirectory),
            pending,
            _log);
    }

    /// <summary>
    /// 启动更新器进程并返回其 PID。调用方随后应退出主程序。
    /// </summary>
    /// <param name="appDirectory">当前 <c>program/</c> 目录。</param>
    /// <param name="contentDirectory">已解压的新版本目录。</param>
    public int? LaunchUpdater(string appDirectory, string contentDirectory)
    {
        var updaterPath = Path.Combine(appDirectory, PortableLayout.UpdaterExecutableName);
        if (!File.Exists(updaterPath))
        {
            _log?.Warn("update.updater.missing", "NMTools.Updater.exe was not found next to the application");
            return null;
        }

        // 更新器不能从 program/ 里运行：它要替换整个 program/，包括它自己的 EXE，
        // 而 Windows 不允许覆盖正在运行的可执行文件。先把它复制到暂存目录再从那里启动。
        var stagingDirectory = Path.Combine(
            PortableLayout.ResolveStagingDirectory(appDirectory),
            "updater");
        string launchPath;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            launchPath = Path.Combine(stagingDirectory, PortableLayout.UpdaterExecutableName);
            File.Copy(updaterPath, launchPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Warn("update.updater.stageFailed", $"ex={exception.GetType().Name}");
            return null;
        }

        try
        {
            // 路径必须以非分隔符结尾：AppContext.BaseDirectory 带尾部反斜杠，
            // 拼成 "D:\...\program\" 后反斜杠会转义右引号，整个命令行被解析错，
            // 更新器收到的参数全乱。这里统一去掉尾部反斜杠。
            var normalizedAppDirectory = appDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var normalizedContent = contentDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = launchPath,
                Arguments = $"--pid {Environment.ProcessId} --app-dir \"{normalizedAppDirectory}\" --staged-dir \"{normalizedContent}\"",
                UseShellExecute = false,
                WorkingDirectory = stagingDirectory,
            });

            if (process is null)
            {
                _log?.Warn("update.updater.startFailed", "process start returned null");
                return null;
            }

            _log?.Info("update.updater.started", $"pid={process.Id}");
            return process.Id;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log?.Warn("update.updater.startFailed", $"ex={exception.GetType().Name}");
            return null;
        }
    }

    private static PrepareFailureReason MapDownloadFailure(UpdateDownloadFailureReason reason) => reason switch
    {
        UpdateDownloadFailureReason.ChecksumMismatch => PrepareFailureReason.ChecksumMismatch,
        UpdateDownloadFailureReason.SizeMismatch => PrepareFailureReason.SizeMismatch,
        UpdateDownloadFailureReason.StorageFailure => PrepareFailureReason.StorageFailure,
        UpdateDownloadFailureReason.Canceled => PrepareFailureReason.Canceled,
        _ => PrepareFailureReason.DownloadFailed,
    };

    private static PrepareFailureReason MapExtractFailure(UpdateExtractFailureReason reason) => reason switch
    {
        UpdateExtractFailureReason.UnsafeEntry => PrepareFailureReason.UnsafeArchive,
        UpdateExtractFailureReason.InvalidArchive => PrepareFailureReason.InvalidArchive,
        UpdateExtractFailureReason.StorageFailure => PrepareFailureReason.StorageFailure,
        UpdateExtractFailureReason.Canceled => PrepareFailureReason.Canceled,
        _ => PrepareFailureReason.InvalidArchive,
    };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
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

    public void Dispose()
    {
        _downloader.Dispose();
    }
}

public enum PrepareFailureReason
{
    None,
    NotSupported,
    ChecksumMismatch,
    SizeMismatch,
    DownloadFailed,
    InvalidArchive,
    UnsafeArchive,
    StorageFailure,
    Canceled,
}

public sealed record PrepareResult(
    bool IsSuccess,
    PrepareFailureReason FailureReason,
    string? ContentDirectory,
    Version? Version)
{
    public static PrepareResult Success(string contentDirectory, Version version) =>
        new(true, PrepareFailureReason.None, contentDirectory, version);

    public static PrepareResult Failure(PrepareFailureReason reason) =>
        new(false, reason, null, null);
}

/// <summary>
/// 「下次启动时更新」的待办记录。写在发布包根目录，不涉及用户设置。
/// </summary>
public sealed record PendingUpdate
{
    public string Version { get; init; } = string.Empty;

    public string ContentDirectory { get; init; } = string.Empty;

    public string AppDirectory { get; init; } = string.Empty;
}

public static class PendingUpdateStore
{
    public static bool Save(string markerPath, PendingUpdate pending, IDiagnosticLog? log)
    {
        try
        {
            var json = JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true });
            return AtomicFileWriter.WriteAllText(markerPath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Warn("update.pending.saveFailed", $"ex={exception.GetType().Name}");
            return false;
        }
    }

    public static PendingUpdate? Load(string appDirectory)
    {
        var markerPath = PortableLayout.ResolvePendingUpdateMarker(appDirectory);
        return AtomicFileWriter.ReadFirstValid(markerPath, json =>
        {
            var pending = JsonSerializer.Deserialize<PendingUpdate>(json);
            return pending is null || string.IsNullOrWhiteSpace(pending.ContentDirectory) ? null : pending;
        });
    }

    public static void Clear(string appDirectory)
    {
        try
        {
            var markerPath = PortableLayout.ResolvePendingUpdateMarker(appDirectory);
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
