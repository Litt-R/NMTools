using System.Windows.Input;
using NMTools.Core;

namespace NMTools.App.Infrastructure;

public static class ModifierKeysExtensions
{
    /// <summary>
    /// 把 WPF 的修饰键转换成 Core 的 <see cref="HotkeyModifiers"/>。
    /// </summary>
    public static HotkeyModifiers ToHotkeyModifiers(this ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Windows;
        return result;
    }
}
