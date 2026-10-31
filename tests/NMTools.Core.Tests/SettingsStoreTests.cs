using NMTools.App.Infrastructure;
using NMTools.Core;

namespace NMTools.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _testDirectory;

    public SettingsStoreTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "NMTools_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void DeleteIfExists(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void AtomicFileWriter_WriteAndRead_Succeeds()
    {
        var filePath = Path.Combine(_testDirectory, "test.json");
        var content = "{\"key\":\"value\"}";

        var writeOk = AtomicFileWriter.WriteAllText(filePath, content);
        Assert.True(writeOk);
        Assert.True(File.Exists(filePath));

        var readContent = AtomicFileWriter.ReadAllText(filePath);
        Assert.Equal(content, readContent);
    }

    [Fact]
    public void AtomicFileWriter_Overwrite_PreservesBackup()
    {
        var filePath = Path.Combine(_testDirectory, "test.json");
        var initial = "{\"v\":1}";
        var updated = "{\"v\":2}";

        AtomicFileWriter.WriteAllText(filePath, initial);
        AtomicFileWriter.WriteAllText(filePath, updated);

        Assert.Equal(updated, AtomicFileWriter.ReadAllText(filePath));
        var backupPath = filePath + ".bak";
        Assert.True(File.Exists(backupPath));
        Assert.Equal(initial, File.ReadAllText(backupPath));
    }

    [Fact]
    public void AppSettingsStore_CorruptedMainFile_RecoversFromBackup()
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            var store = new AppSettingsStore(fileName);
            var first = new InputBridgeOptions { HotkeyVirtualKey = 0x41 };
            var second = new InputBridgeOptions { HotkeyVirtualKey = 0x42 };
            Assert.True(store.Save(first));
            Assert.True(store.Save(second));

            // 模拟断电或被截断：主文件还在，但内容已不是合法 JSON。
            File.WriteAllText(path, "{ \"Version\": 6, \"Hotkey");

            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(first.HotkeyVirtualKey, loaded!.HotkeyVirtualKey);
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }

    [Fact]
    public void AppSettingsStore_CorruptedMainAndNoBackup_ReturnsNull()
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            var store = new AppSettingsStore(fileName);
            File.WriteAllText(path, "not json");

            Assert.Null(store.Load());
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }

    [Fact]
    public void WindowSettingsStore_CorruptedMainFile_RecoversFromBackup()
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            var store = new WindowSettingsStore(fileName);
            store.Save(new WindowSettings { Left = 100, Top = 200, Width = 500, Height = 310 });
            store.Save(new WindowSettings { Left = 300, Top = 400, Width = 500, Height = 310 });

            File.WriteAllText(path, "{ \"Version\": 1, \"Left\":");

            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(100, loaded!.Left);
            Assert.Equal(200, loaded.Top);
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }

    [Fact]
    public void AtomicFileWriter_ReadFirstValid_SkipsInvalidMainAndUsesBackup()
    {
        var filePath = Path.Combine(_testDirectory, "test.json");
        AtomicFileWriter.WriteAllText(filePath, "good");
        AtomicFileWriter.WriteAllText(filePath, "bad");

        var result = AtomicFileWriter.ReadFirstValid(filePath, text => text == "good" ? text : null);

        Assert.Equal("good", result);
    }

    [Fact]
    public void AtomicFileWriter_ReadFirstValid_PrefersValidMainOverBackup()
    {
        var filePath = Path.Combine(_testDirectory, "test.json");
        AtomicFileWriter.WriteAllText(filePath, "old");
        AtomicFileWriter.WriteAllText(filePath, "new");

        var result = AtomicFileWriter.ReadFirstValid(filePath, text => text);

        Assert.Equal("new", result);
    }

    [Fact]
    public void AtomicFileWriter_ReadFirstValid_MissingFiles_ReturnsNull()
    {
        var filePath = Path.Combine(_testDirectory, "missing.json");

        Assert.Null(AtomicFileWriter.ReadFirstValid(filePath, text => text));
    }

    [Fact]
    public void AtomicFileWriter_ReadFirstValid_JsonExceptionFromParser_FallsBack()
    {
        var filePath = Path.Combine(_testDirectory, "test.json");
        AtomicFileWriter.WriteAllText(filePath, "{\"v\":1}");
        AtomicFileWriter.WriteAllText(filePath, "{broken");

        var result = AtomicFileWriter.ReadFirstValid(
            filePath,
            text => System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("v").GetInt32() as int?);

        Assert.Equal(1, result);
    }

    [Fact]
    public void AtomicFileWriter_CorruptedMainFile_RecoversFromBackup()
    {
        var filePath = Path.Combine(_testDirectory, "test.json");
        var initial = "{\"v\":1}";
        var updated = "{\"v\":2}";

        AtomicFileWriter.WriteAllText(filePath, initial);
        AtomicFileWriter.WriteAllText(filePath, updated);

        // 模拟主文件被意外损坏/删除
        File.Delete(filePath);

        var recovered = AtomicFileWriter.ReadAllText(filePath);
        Assert.Equal(initial, recovered);
    }

    [Fact]
    public void AppSettingsStore_PersistsRetryHotkeyAndSettleDelay()
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            var store = new AppSettingsStore(fileName);
            var options = new InputBridgeOptions
            {
                HotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                HotkeyVirtualKey = 0x20,
                RetryHotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
                RetryHotkeyVirtualKey = 0x52,
                PostActivationClipboardSettleDelay = TimeSpan.FromMilliseconds(650),
            };

            Assert.True(store.Save(options));
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, loaded!.RetryHotkeyModifiers);
            Assert.Equal(0x52u, loaded.RetryHotkeyVirtualKey);
            Assert.Equal(TimeSpan.FromMilliseconds(650), loaded.PostActivationClipboardSettleDelay);
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }

    [Fact]
    public void AppSettingsStore_CheckUpdatesOnStartup_DefaultsToFalse()
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            var store = new AppSettingsStore(fileName);

            Assert.True(store.Save(new InputBridgeOptions()));
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.False(loaded!.CheckUpdatesOnStartup);
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }

    [Fact]
    public void AppSettingsStore_CheckUpdatesOnStartup_RoundTripsWhenEnabled()
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            var store = new AppSettingsStore(fileName);

            Assert.True(store.Save(new InputBridgeOptions { CheckUpdatesOnStartup = true }));
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.True(loaded!.CheckUpdatesOnStartup);
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }

    [Fact]
    public void AppSettingsStore_Version6SettingsWithoutUpdateFlag_LoadAsDisabled()
    {
        // 上一版写出的设置文件没有 CheckUpdatesOnStartup 字段，必须能正常读取且默认不联网。
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 版本 6 的设置一定包含 PasteBindings（缺失会被判为损坏），只是没有更新开关字段。
            var legacy = System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 6,
                HotkeyModifiers = 3,
                HotkeyVirtualKey = 32,
                ClipboardSettleDelayMilliseconds = 400,
                EnableVisualEffects = true,
                MaxInputLength = 100000,
                PasteBindings = new PasteShortcutBindings(),
            });
            File.WriteAllText(path, legacy);

            var loaded = new AppSettingsStore(fileName).Load();

            Assert.NotNull(loaded);
            Assert.False(loaded!.CheckUpdatesOnStartup);
            Assert.Equal(0x20u, loaded.HotkeyVirtualKey);
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }

    [Fact]
    public void AppSettingsStore_DisabledRetryHotkey_RoundTripsAsDisabled()
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        try
        {
            var store = new AppSettingsStore(fileName);
            var options = new InputBridgeOptions
            {
                RetryHotkeyModifiers = HotkeyModifiers.None,
                RetryHotkeyVirtualKey = 0,
            };

            Assert.True(store.Save(options));
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.False(loaded!.HasRetryHotkey);
            Assert.Equal(HotkeyModifiers.None, loaded.RetryHotkeyModifiers);
            Assert.Equal(0u, loaded.RetryHotkeyVirtualKey);
        }
        finally
        {
            DeleteIfExists(path, path + ".bak");
        }
    }
}
