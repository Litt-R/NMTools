using System.IO;
using System.Text.Json;

namespace NMTools.App.Infrastructure;

public static class AtomicFileWriter
{
    public static bool WriteAllText(string targetPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
        var backupPath = targetPath + ".bak";

        try
        {
            File.WriteAllText(tempPath, content);

            if (File.Exists(targetPath))
            {
                try
                {
                    File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(targetPath, backupPath, overwrite: true);
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                catch (IOException)
                {
                    File.Copy(targetPath, backupPath, overwrite: true);
                    File.Move(tempPath, targetPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 清理临时文件失败不覆盖原始异常
            }

            return false;
        }
    }

    /// <summary>
    /// 依次尝试主文件和 .bak 备份，返回第一个能被 <paramref name="parse"/> 成功解析的结果。
    /// 主文件存在但内容已损坏（断电写一半、被截断）时，<see cref="ReadAllText"/> 只会原样返回坏内容，
    /// 因此是否有效必须由调用方的解析器判断：解析器返回 null 或抛出 <see cref="JsonException"/> 都视为无效。
    /// </summary>
    public static T? ReadFirstValid<T>(string targetPath, Func<string, T?> parse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(parse);

        foreach (var path in new[] { targetPath, targetPath + ".bak" })
        {
            string text;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                text = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            try
            {
                var result = parse(text);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (JsonException)
            {
                // 内容损坏，继续尝试备份。
            }
        }

        return default;
    }

    public static string? ReadAllText(string targetPath)
    {
        if (File.Exists(targetPath))
        {
            try
            {
                return File.ReadAllText(targetPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 尝试从备份读取
            }
        }

        var backupPath = targetPath + ".bak";
        if (File.Exists(backupPath))
        {
            try
            {
                return File.ReadAllText(backupPath);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
