using NMTools.Core;

namespace NMTools.App.Infrastructure;

public static class HotkeyFormatter
{
    public static string Format(InputBridgeOptions options)
    {
        return string.Join(" + ", FormatParts(options));
    }

    public static IReadOnlyList<string> FormatParts(InputBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FormatParts(new PasteShortcut(options.HotkeyModifiers, options.HotkeyVirtualKey));
    }

    public static string FormatRetry(InputBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.HasRetryHotkey
            ? string.Join(" + ", FormatRetryParts(options))
            : "未启用";
    }

    public static IReadOnlyList<string> FormatRetryParts(InputBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasRetryHotkey)
        {
            return Array.Empty<string>();
        }

        return FormatParts(new PasteShortcut(options.RetryHotkeyModifiers, options.RetryHotkeyVirtualKey));
    }

    public static string Format(PasteShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        return string.Join(" + ", FormatParts(shortcut));
    }

    public static string Format(RemoteInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var parts = new List<string>(FormatModifiers(input.Modifiers));
        parts.Add(input.MouseButton switch
        {
            RemoteMouseButton.Middle => "鼠标中键",
            RemoteMouseButton.Right => "鼠标右键",
            _ => FormatKey(input.VirtualKey),
        });
        return string.Join(" + ", parts);
    }

    public static IReadOnlyList<string> FormatParts(PasteShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);

        var parts = new List<string>(FormatModifiers(shortcut.Modifiers));
        parts.Add(FormatKey(shortcut.VirtualKey));
        return parts;
    }

    private static IReadOnlyList<string> FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        return parts;
    }

    private static string FormatKey(uint virtualKey)
    {
        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x70 and <= 0x7B => $"F{virtualKey - 0x6F}",
            _ => $"0x{virtualKey:X2}",
        };
    }
}
