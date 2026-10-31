using System.IO;
using System.Text.Json;

namespace NMTools.App.Infrastructure;

public sealed class WindowSettingsStore
{
    private const int CurrentVersion = 1;
    private readonly string _settingsPath;

    public WindowSettingsStore(string fileName = "window-settings.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var dataDirectory = AppSettingsStore.GetDataDirectory();

        try
        {
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // 保持路径位于程序目录；设置保存失败时不影响输入桥接。
        }

        _settingsPath = Path.Combine(dataDirectory, fileName);
    }

    public WindowSettings? Load()
    {
        // 主文件内容损坏时回退到 .bak 备份。
        return AtomicFileWriter.ReadFirstValid(_settingsPath, json =>
        {
            var settings = JsonSerializer.Deserialize<WindowSettings>(json);
            return settings?.Version == CurrentVersion ? settings : null;
        });
    }

    public void Save(WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var json = JsonSerializer.Serialize(
                settings with { Version = CurrentVersion },
                new JsonSerializerOptions { WriteIndented = true });
            AtomicFileWriter.WriteAllText(_settingsPath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 窗口位置记忆不是核心功能，写入失败不能影响输入桥接。
        }
    }
}

public sealed record WindowSettings
{
    public int Version { get; init; }

    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }
}
