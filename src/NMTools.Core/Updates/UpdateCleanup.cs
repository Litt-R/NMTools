using System.Text.Json;
using System.Text.Json.Serialization;

namespace NMTools.Core.Updates;

/// <summary>
/// 更新完成后的清理：删除旧版本备份与暂存目录。
///
/// 时机很关键——必须在新版本**成功启动之后**才能删备份，否则一旦新版本起不来，
/// 就没有可回滚的版本了。调用方（主程序）在启动流程末尾调用 <see cref="RunIfUpdateCompleted"/>。
/// </summary>
public static class UpdateCleanup
{
    /// <summary>清理结果，供日志记录。</summary>
    public enum Outcome
    {
        /// <summary>没有待确认的更新，什么也没做。</summary>
        NothingToDo,

        /// <summary>清理完成。</summary>
        Cleaned,

        /// <summary>标记存在但内容无法解析，按无待确认处理并清掉标记。</summary>
        InvalidMarker,
    }

    /// <summary>
    /// 如果存在「更新已完成」标记，说明本次启动的是刚替换进来的新版本。
    /// 启动走到这里就证明它能正常起来，可以删除备份与暂存目录了。
    /// </summary>
    /// <param name="appDirectory">当前 <c>program/</c> 目录。</param>
    /// <param name="log">可选的日志回调。</param>
    public static Outcome RunIfUpdateCompleted(string appDirectory, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        var markerPath = PortableLayout.ResolveVerifyUpdateMarker(appDirectory);
        if (!File.Exists(markerPath))
        {
            return Outcome.NothingToDo;
        }

        // 标记存在即视为新版本启动成功。即使内容损坏也不该卡住清理——
        // 标记的作用只是"这里有一次更新刚完成"，不含需要校验的决策信息。
        var outcome = Outcome.Cleaned;
        try
        {
            var json = File.ReadAllText(markerPath);
            if (string.IsNullOrWhiteSpace(json) || !IsValidMarker(json))
            {
                outcome = Outcome.InvalidMarker;
                log?.Invoke("cleanup.invalidMarker");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            outcome = Outcome.InvalidMarker;
            log?.Invoke($"cleanup.markerUnreadable ex={exception.GetType().Name}");
        }

        TryDeleteDirectory(PortableLayout.ResolveBackupDirectory(appDirectory), log, "cleanup.backupDeleted");
        TryDeleteDirectory(PortableLayout.ResolveStagingDirectory(appDirectory), log, "cleanup.stagingDeleted");
        TryDeleteFile(markerPath, log, "cleanup.markerDeleted");

        return outcome;
    }

    /// <summary>
    /// 写入「更新已完成」标记。由更新器在替换成功后调用。
    /// </summary>
    public static bool WriteVerifyMarker(string appDirectory, Version newVersion, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(newVersion);

        var marker = new VerifyMarker
        {
            Version = ReleaseVersion.Normalize(newVersion).ToString(),
            CompletedAt = DateTimeOffset.Now,
        };

        try
        {
            var path = PortableLayout.ResolveVerifyUpdateMarker(appDirectory);
            var json = JsonSerializer.Serialize(marker, UpdateJsonContext.Default.VerifyMarker);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 写不了标记不是致命问题：新版本仍可运行，只是备份不会被自动清理。
            log?.Invoke($"cleanup.markerWriteFailed ex={exception.GetType().Name}");
            return false;
        }
    }

    private static bool IsValidMarker(string json)
    {
        try
        {
            var marker = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.VerifyMarker);
            return marker is not null && !string.IsNullOrWhiteSpace(marker.Version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path, Action<string>? log, string eventName)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            Directory.Delete(path, recursive: true);
            log?.Invoke(eventName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删不掉不致命：下次更新会覆盖，或者用户手动清理。
            log?.Invoke($"{eventName}.failed ex={exception.GetType().Name}");
        }
    }

    private static void TryDeleteFile(string path, Action<string>? log, string eventName)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                log?.Invoke(eventName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"{eventName}.failed ex={exception.GetType().Name}");
        }
    }

    internal sealed record VerifyMarker
    {
        public string Version { get; init; } = string.Empty;

        public DateTimeOffset CompletedAt { get; init; }
    }
}

/// <summary>
/// 更新相关的 JSON 序列化上下文。
///
/// 必须用源生成而不是反射：更新器启用了 trimming，反射式序列化会被 IL2026 拒绝，
/// 而且裁剪后可能把需要的类型裁掉导致运行时失败。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateCleanup.VerifyMarker))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext
{
}
