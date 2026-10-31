using System.IO;
using System.Text.Json;
using NMTools.Core;

namespace NMTools.App.Infrastructure;

/// <summary>
/// 保存不包含输入正文的应用设置。设置始终位于单文件程序目录下。
///
/// 版本兼容策略见 <see cref="SettingsSchema"/>：读取范围由 Schema 决定，
/// 字段级迁移集中在本文件的 <c>Parse</c> 里按版本逐条判断。
/// 读到旧格式成功迁移后会立即按当前格式回写，避免每次启动重复迁移。
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _settingsPath;

    public AppSettingsStore(string fileName = "input-bridge-settings.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        _settingsPath = SettingsPathResolver.Resolve(fileName);
    }

    public InputBridgeOptions? Load()
    {
        // 主文件内容损坏（不是合法 JSON，或字段不合法）时，回退到 .bak 备份。
        var loaded = AtomicFileWriter.ReadFirstValid(_settingsPath, Parse);
        if (loaded is null)
        {
            return null;
        }

        // 读到旧格式就立刻按当前格式落盘。否则用户不打开设置窗口就不会触发保存，
        // 旧文件一直留在磁盘上，每次启动都要重新迁移一遍。
        // 落盘失败不影响本次使用：内存里的迁移结果已经可用，下次启动会再试。
        if (loaded.MigratedFromVersion is not null)
        {
            Save(loaded.Options);
        }

        return loaded.Options;
    }

    private static LoadedSettings? Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<PersistedAppSettings>(json);
        if (settings is null || !SettingsSchema.IsSupported(settings.Version))
        {
            return null;
        }

        // 版本 5 的自定义槽位使用旧模型，需要转换成"左右两侧都设置"的新模型。
        var pasteBindings = settings.PasteBindings;
        if (SettingsSchema.NeedsLegacyCustomMigration(settings.Version))
        {
            pasteBindings = pasteBindings?.MigrateLegacyCustom();
        }

        // 从版本 5 起才有粘贴方式槽位；更早的版本补全套默认绑定。
        var hasPasteBindings = SettingsSchema.HasPasteBindings(settings.Version);
        if (!HotkeyValidation.IsValidGlobalHotkey(settings.HotkeyModifiers, settings.HotkeyVirtualKey)
            || (hasPasteBindings && !ArePasteBindingsValid(pasteBindings)))
        {
            return null;
        }

        var defaults = new InputBridgeOptions();
        var (retryModifiers, retryVirtualKey) = ResolveRetryHotkey(settings, defaults);
        var settleDelayMs = settings.ClipboardSettleDelayMilliseconds is int configuredDelay
            && InputBridgeOptions.IsValidClipboardSettleDelayMilliseconds(configuredDelay)
                ? configuredDelay
                : InputBridgeOptions.DefaultClipboardSettleDelayMilliseconds;

        var options = new InputBridgeOptions
        {
            HotkeyModifiers = settings.HotkeyModifiers,
            HotkeyVirtualKey = settings.HotkeyVirtualKey,
            RetryHotkeyModifiers = retryModifiers,
            RetryHotkeyVirtualKey = retryVirtualKey,
            PostActivationClipboardSettleDelay = TimeSpan.FromMilliseconds(settleDelayMs),
            EnableVisualEffects = settings.EnableVisualEffects ?? defaults.EnableVisualEffects,
            // 版本 7 之前的文件没有这个字段，反序列化为 null，按"不勾选"处理。
            CheckUpdatesOnStartup = settings.CheckUpdatesOnStartup ?? defaults.CheckUpdatesOnStartup,
            MaxInputLength = IsInputLengthValid(settings.MaxInputLength)
                ? settings.MaxInputLength
                : defaults.MaxInputLength,
            PasteBindings = hasPasteBindings
                ? pasteBindings ?? new PasteShortcutBindings()
                : new PasteShortcutBindings(),
        };

        // 只有旧版本才需要回写；已经是当前版本的文件原样读取，不产生多余的磁盘写入。
        int? migratedFrom = settings.Version < SettingsSchema.CurrentVersion ? settings.Version : null;
        return new LoadedSettings(options, migratedFrom);
    }

    private static (HotkeyModifiers Modifiers, uint VirtualKey) ResolveRetryHotkey(
        PersistedAppSettings settings,
        InputBridgeOptions defaults)
    {
        if (settings.RetryHotkeyModifiers is not null || settings.RetryHotkeyVirtualKey is not null)
        {
            var modifiers = settings.RetryHotkeyModifiers ?? HotkeyModifiers.None;
            var virtualKey = settings.RetryHotkeyVirtualKey ?? 0;
            if (HotkeyValidation.IsValidOptionalGlobalHotkey(modifiers, virtualKey)
                && (virtualKey == 0
                    || modifiers != settings.HotkeyModifiers
                    || virtualKey != settings.HotkeyVirtualKey))
            {
                return (modifiers, virtualKey);
            }
        }

        if (defaults.RetryHotkeyModifiers == settings.HotkeyModifiers
            && defaults.RetryHotkeyVirtualKey == settings.HotkeyVirtualKey)
        {
            return (HotkeyModifiers.None, 0);
        }

        return (defaults.RetryHotkeyModifiers, defaults.RetryHotkeyVirtualKey);
    }

    public bool Save(InputBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var settings = new PersistedAppSettings
            {
                Version = SettingsSchema.CurrentVersion,
                HotkeyModifiers = options.HotkeyModifiers,
                HotkeyVirtualKey = options.HotkeyVirtualKey,
                RetryHotkeyModifiers = options.RetryHotkeyModifiers,
                RetryHotkeyVirtualKey = options.RetryHotkeyVirtualKey,
                ClipboardSettleDelayMilliseconds =
                    (int)Math.Round(options.PostActivationClipboardSettleDelay.TotalMilliseconds),
                EnableVisualEffects = options.EnableVisualEffects,
                CheckUpdatesOnStartup = options.CheckUpdatesOnStartup,
                MaxInputLength = options.MaxInputLength,
                PasteBindings = options.PasteBindings,
            };
            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions { WriteIndented = true });
            return AtomicFileWriter.WriteAllText(_settingsPath, json);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析结果。带上原始版本号，便于调用方判断是否需要回写到磁盘。
    /// </summary>
    private sealed record LoadedSettings(InputBridgeOptions Options, int? MigratedFromVersion);

    private sealed record PersistedAppSettings
    {
        public int Version { get; init; }

        public HotkeyModifiers HotkeyModifiers { get; init; }

        public uint HotkeyVirtualKey { get; init; }

        public HotkeyModifiers? RetryHotkeyModifiers { get; init; }

        public uint? RetryHotkeyVirtualKey { get; init; }

        public int? ClipboardSettleDelayMilliseconds { get; init; }

        public bool? EnableVisualEffects { get; init; }

        public bool? CheckUpdatesOnStartup { get; init; }

        public int MaxInputLength { get; init; }

        public PasteShortcutBindings? PasteBindings { get; init; }
    }

    private static bool IsInputLengthValid(int length)
    {
        return length is >= 1 and <= 1_000_000;
    }

    private static bool ArePasteBindingsValid(PasteShortcutBindings? bindings)
    {
        if (bindings is null)
        {
            return false;
        }

        return bindings.IsValid();
    }

    internal static string GetDataDirectory()
    {
        return (System.Windows.Application.Current as App)?.IsUiAutomationMode == true
            ? Path.Combine(AppContext.BaseDirectory, ".ui-test-data")
            : Path.Combine(AppContext.BaseDirectory, "data");
    }

    private static class SettingsPathResolver
    {
        public static string Resolve(string fileName)
        {
            var dataDirectory = GetDataDirectory();

            try
            {
                Directory.CreateDirectory(dataDirectory);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                // 保持路径位于程序目录；保存失败时由调用方按非核心设置处理。
            }

            return Path.Combine(dataDirectory, fileName);
        }
    }
}
