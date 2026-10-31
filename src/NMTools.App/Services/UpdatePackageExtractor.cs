using System.IO;
using System.IO.Compression;
using NMTools.Core.Updates;

namespace NMTools.App.Services;

/// <summary>
/// 解压更新包。所有路径校验走 <see cref="PortableLayout.ResolveSafeEntryPath"/>，
/// 并额外限制条目数量与解压后总体积，防止 zip 炸弹。
/// </summary>
public sealed class UpdatePackageExtractor : IUpdatePackageExtractor
{
    /// <summary>解压后的条目上限。便携包当前只有几个文件，给足余量但不允许无限增长。</summary>
    public const int MaxEntryCount = 1000;

    /// <summary>解压后总字节上限，约为压缩包上限的四倍。</summary>
    public const long MaxTotalBytes = 800L * 1024 * 1024;

    private readonly Action<string>? _log;

    public UpdatePackageExtractor(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// 从解压根目录里找出真正的包根：含 <c>program/NMTools.App.exe</c> 的那一层。
    ///
    /// 发布包由 <c>Compress-Archive</c> 生成，会在 zip 里套一层版本目录；
    /// 这里同时接受"套一层"和"没有套"两种形状，避免因为打包方式变化就更新不了。
    /// </summary>
    private static string? ResolvePackageRoot(string extractedRoot)
    {
        if (HasAppExecutable(extractedRoot))
        {
            return extractedRoot;
        }

        // 只向下看一层：再深就不是发布包的结构了。
        DirectoryInfo[] children;
        try
        {
            children = new DirectoryInfo(extractedRoot).GetDirectories();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var child in children)
        {
            if (HasAppExecutable(child.FullName))
            {
                return child.FullName;
            }
        }

        return null;
    }

    private static bool HasAppExecutable(string root) =>
        File.Exists(Path.Combine(root, PortableLayout.AppDirectoryName, PortableLayout.AppExecutableName))
        || File.Exists(Path.Combine(root, PortableLayout.AppExecutableName));

    public UpdateExtractResult Extract(string archivePath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        try
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }

            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"extract.prepare failed: {exception.GetType().Name}");
            return UpdateExtractResult.Failure(UpdateExtractFailureReason.StorageFailure);
        }

        var root = Path.GetFullPath(destinationDirectory);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            // 先整体校验再解压：发现任何不安全条目就完全不动磁盘，
            // 避免"解了一半才发现恶意路径"的中间状态。
            var entries = new List<(ZipArchiveEntry Entry, string Target)>();
            long totalBytes = 0;

            if (archive.Entries.Count > MaxEntryCount)
            {
                _log?.Invoke($"extract.tooManyEntries count={archive.Entries.Count}");
                return UpdateExtractResult.Failure(UpdateExtractFailureReason.LimitExceeded);
            }

            foreach (var entry in archive.Entries)
            {
                // 目录条目（以 / 结尾）不需要单独创建，解压文件时会自动建目录。
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var relative = PortableLayout.ResolveSafeEntryPath(entry.FullName);
                if (relative is null)
                {
                    _log?.Invoke("extract.unsafeEntry");
                    return UpdateExtractResult.Failure(UpdateExtractFailureReason.UnsafeEntry);
                }

                var target = Path.GetFullPath(Path.Combine(root, relative));

                // 双保险：规范化后必须仍在目标目录内。符号链接或畸形条目可能绕过前缀检查。
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke("extract.escapedEntry");
                    return UpdateExtractResult.Failure(UpdateExtractFailureReason.UnsafeEntry);
                }

                totalBytes += entry.Length;
                if (totalBytes > MaxTotalBytes)
                {
                    _log?.Invoke($"extract.tooLarge bytes={totalBytes}");
                    return UpdateExtractResult.Failure(UpdateExtractFailureReason.LimitExceeded);
                }

                entries.Add((entry, target));
            }

            foreach (var (entry, target) in entries)
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                entry.ExtractToFile(target, overwrite: false);
            }

            // 发布包在 zip 里套了一层版本目录（NMTools-v0.7.0-win-x64/），
            // 解压后真正的根目录是它。定位到含 program/NMTools.App.exe 的那一层，
            // 找不到才判定包不完整。
            var packageRoot = ResolvePackageRoot(root);
            if (packageRoot is null)
            {
                _log?.Invoke("extract.missingAppExecutable");
                return UpdateExtractResult.Failure(UpdateExtractFailureReason.MissingRequiredFile);
            }

            _log?.Invoke($"extract.completed entries={entries.Count} root={Path.GetFileName(packageRoot)}");
            return UpdateExtractResult.Success(packageRoot);
        }
        catch (InvalidDataException)
        {
            // zip 格式损坏或不是 zip。
            _log?.Invoke("extract.invalidArchive");
            return UpdateExtractResult.Failure(UpdateExtractFailureReason.InvalidArchive);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"extract.storageFailed: {exception.GetType().Name}");
            return UpdateExtractResult.Failure(UpdateExtractFailureReason.StorageFailure);
        }
    }
}
