namespace NMTools.Core;

/// <summary>
/// 快捷键的结构合法性判断，供全局热键注册、设置文件读取和气泡槽位校验共用。
/// 只检查数值范围和修饰键位，不判断快捷键之间是否冲突。
/// </summary>
public static class HotkeyValidation
{
    /// <summary>Windows 虚拟键码的最大值（含）。0 不是有效按键。</summary>
    public const uint MaxVirtualKey = 0xFF;

    public static bool IsVirtualKeyInRange(uint virtualKey)
    {
        return virtualKey is not 0 and <= MaxVirtualKey;
    }

    public static bool AreModifiersDefined(HotkeyModifiers modifiers)
    {
        return (modifiers & ~HotkeyModifiers.All) == 0;
    }

    /// <summary>
    /// 全局热键必须至少带一个修饰键：单独的字母或空格会吞掉所有应用里的正常输入。
    /// </summary>
    public static bool IsValidGlobalHotkey(HotkeyModifiers modifiers, uint virtualKey)
    {
        return IsVirtualKeyInRange(virtualKey)
            && modifiers != HotkeyModifiers.None
            && AreModifiersDefined(modifiers);
    }

    /// <summary>
    /// 可选全局热键：修饰键和按键都为 0 时表示未启用；一旦设置了任一侧，则必须满足完整全局热键要求。
    /// </summary>
    public static bool IsValidOptionalGlobalHotkey(HotkeyModifiers modifiers, uint virtualKey)
    {
        if (modifiers == HotkeyModifiers.None && virtualKey == 0)
        {
            return true;
        }

        return IsValidGlobalHotkey(modifiers, virtualKey);
    }
}
