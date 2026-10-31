using System.IO;
using System.Text.Json;
using NMTools.Core;

namespace NMTools.App.Infrastructure;

/// <summary>
/// 保存不包含输入正文的应用设置。设置始终位于单文件程序目录下。
/// </summary>
public sealed class AppSettingsStore
{
    private const int CurrentVersion = 7;
    private const int LegacyUpdateSettingsVersion = 6;
    private const int LegacyCustomSlotVersion = 5;
    private readonly string _settingsPath;

    public AppSettingsStore(string fileName = "input-bridge-settings.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        _settingsPath = SettingsPathResolver.Resolve(fileName);
    }

    public InputBridgeOptions? Load()
    {
        // 主文件内容损坏（不是合法 JSON，或字段不合法）时，回退到 .bak 备份。
        return AtomicFileWriter.ReadFirstValid(_settingsPath, Parse);
    }

    private static InputBridgeOptions? Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<PersistedAppSettings>(json);
        if (settings is null || settings.Version is not (1 or 2 or 3 or 4 or LegacyCustomSlotVersion or LegacyUpdateSettingsVersion or CurrentVersion))
        {
            return null;
        }

        var pasteBindings = settings.PasteBindings;
        if (settings.Version == LegacyCustomSlotVersion)
        {
            pasteBindings = pasteBindings?.MigrateLegacyCustom();
        }

        var hasPasteBindings = settings.Version is >= LegacyCustomSlotVersion;
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

        return new InputBridgeOptions
        {
            HotkeyModifiers = settings.HotkeyModifiers,
            HotkeyVirtualKey = settings.HotkeyVirtualKey,
            RetryHotkeyModifiers = retryModifiers,
            RetryHotkeyVirtualKey = retryVirtualKey,
            PostActivationClipboardSettleDelay = TimeSpan.FromMilliseconds(settleDelayMs),
            EnableVisualEffects = settings.EnableVisualEffects ?? defaults.EnableVisualEffects,
            // 旧版本设置没有该字段，反序列化为 null，按"不勾选"处理。
            CheckUpdatesOnStartup = settings.CheckUpdatesOnStartup ?? defaults.CheckUpdatesOnStartup,
            MaxInputLength = IsInputLengthValid(settings.MaxInputLength)
                ? settings.MaxInputLength
                : defaults.MaxInputLength,
            PasteBindings = hasPasteBindings
                ? pasteBindings ?? new PasteShortcutBindings()
                : new PasteShortcutBindings(),
        };
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
                Version = CurrentVersion,
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
