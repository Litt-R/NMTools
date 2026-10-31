namespace NMTools.Core;

/// <summary>
/// 配置本地输入桥接行为，不包含任何 Windows 平台类型。
/// </summary>
public sealed record InputBridgeOptions
{
    public const int DefaultClipboardSettleDelayMilliseconds = 400;
    public const int MinClipboardSettleDelayMilliseconds = 50;
    public const int MaxClipboardSettleDelayMilliseconds = 5000;

    public HotkeyModifiers HotkeyModifiers { get; init; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    public uint HotkeyVirtualKey { get; init; } = 0x20;

    /// <summary>
    /// 全局重试粘贴快捷键。默认 Ctrl + Alt + V；当 RetryHotkeyVirtualKey 为 0 时表示未启用。
    /// </summary>
    public HotkeyModifiers RetryHotkeyModifiers { get; init; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    public uint RetryHotkeyVirtualKey { get; init; } = 0x56;

    public bool HasRetryHotkey =>
        HotkeyValidation.IsValidGlobalHotkey(RetryHotkeyModifiers, RetryHotkeyVirtualKey);

    public static bool IsValidClipboardSettleDelayMilliseconds(int milliseconds)
    {
        return milliseconds is >= MinClipboardSettleDelayMilliseconds and <= MaxClipboardSettleDelayMilliseconds;
    }

    /// <summary>
    /// 输入气泡内五个预置功能槽位和一个自定义槽位的快捷键与远程动作。
    /// </summary>
    public PasteShortcutBindings PasteBindings { get; init; } = new();

    public int MaxInputLength { get; init; } = 100_000;

    public TimeSpan PasteDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan PreActivationClipboardDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan PostActivationClipboardRewriteDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan PostActivationClipboardSettleDelay { get; init; } =
        TimeSpan.FromMilliseconds(DefaultClipboardSettleDelayMilliseconds);

    /// <summary>
    /// 是否启用界面动画与同步状态胶囊提示。默认开启；关闭后气泡无淡入动画、不显示同步粘贴中的小胶囊。
    /// </summary>
    public bool EnableVisualEffects { get; init; } = true;

    /// <summary>
    /// 启动时是否异步检查更新。默认关闭，必须由用户主动勾选；勾选后每次启动只查一次，不轮询。
    /// </summary>
    public bool CheckUpdatesOnStartup { get; init; }

    public bool RequireNoMachineTarget { get; init; } = true;
}

public enum PasteShortcutAction
{
    Enter,
    ControlShiftV,
    OpenCode,
    Emacs,
    Gvim,
    GenericTerminal,
    Custom,
}

public sealed record PasteShortcutBindings
{
    public PasteShortcutBinding Enter { get; init; } = new(
        new PasteShortcut(HotkeyModifiers.None, 0x0D),
        PasteMethod.ControlV);

    // 属性名沿用旧版本以兼容已保存的设置；当前对应 Ctrl + Enter 槽位。
    public PasteShortcutBinding ControlShiftV { get; init; } = new(
        new PasteShortcut(HotkeyModifiers.Control, 0x0D),
        PasteMethod.ControlShiftV);

    public PasteShortcutBinding OpenCode { get; init; } = new(
        new PasteShortcut(HotkeyModifiers.Control, 0x4F),
        PasteMethod.ShiftMiddleClick);

    public PasteShortcutBinding Emacs { get; init; } = new(
        new PasteShortcut(HotkeyModifiers.Control, 0x59),
        PasteMethod.EmacsYank);

    public PasteShortcutBinding Gvim { get; init; } = new(
        new PasteShortcut(HotkeyModifiers.Control, 0x47),
        PasteMethod.GvimRegisterPaste);

    // 属性名沿用首次引入时的命名以兼容已保存的设置；当前对应 Ctrl + T 槽位。
    public PasteShortcutBinding GenericTerminal { get; init; } = new(
        new PasteShortcut(HotkeyModifiers.Control, 0x54),
        PasteMethod.MiddleClick);

    public PasteShortcutBinding Custom { get; init; } = new(null, PasteMethod.None);

    public PasteShortcutAction? Resolve(PasteShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        foreach (var entry in GetEntries())
        {
            if (entry.Binding.Shortcut != shortcut)
            {
                continue;
            }

            if (entry.Action == PasteShortcutAction.Custom && entry.Binding.Method != PasteMethod.CustomInput)
            {
                continue;
            }

            return entry.Action;
        }

        return null;
    }

    public PasteShortcutBindings SetCustomInput(RemoteInput? input)
    {
        return this with { Custom = NormalizeCustom(Custom with { CustomInput = input }) };
    }

    /// <summary>
    /// 把旧版本自定义槽位中的预置粘贴方式转换为等价的自定义输入；无法用单个按键表达的方式会清空该槽位。
    /// </summary>
    public PasteShortcutBindings MigrateLegacyCustom()
    {
        var custom = Custom;
        if (custom.Method is PasteMethod.None or PasteMethod.CustomInput)
        {
            return this with { Custom = NormalizeCustom(custom) };
        }

        RemoteInput? input = custom.Method switch
        {
            PasteMethod.ControlV => new RemoteInput(HotkeyModifiers.Control, 0x56, RemoteMouseButton.None),
            PasteMethod.ControlShiftV => new RemoteInput(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56, RemoteMouseButton.None),
            PasteMethod.EmacsYank => new RemoteInput(HotkeyModifiers.Control, 0x59, RemoteMouseButton.None),
            PasteMethod.ShiftMiddleClick => new RemoteInput(HotkeyModifiers.Shift, 0, RemoteMouseButton.Middle),
            PasteMethod.MiddleClick => new RemoteInput(HotkeyModifiers.None, 0, RemoteMouseButton.Middle),
            _ => null,
        };

        return input is null
            ? this with { Custom = new PasteShortcutBinding(null, PasteMethod.None) }
            : this with { Custom = NormalizeCustom(custom with { CustomInput = input }) };
    }

    public bool HasConflict(PasteShortcut shortcut, PasteShortcutAction? excludedAction = null)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        return GetEntries().Any(entry => entry.Action != excludedAction && entry.Binding.Shortcut == shortcut);
    }

    public PasteShortcutBindings SetShortcut(PasteShortcutAction action, PasteShortcut? shortcut)
    {
        return action switch
        {
            PasteShortcutAction.Enter => this with { Enter = Enter with { Shortcut = shortcut } },
            PasteShortcutAction.ControlShiftV => this with { ControlShiftV = ControlShiftV with { Shortcut = shortcut } },
            PasteShortcutAction.OpenCode => this with { OpenCode = OpenCode with { Shortcut = shortcut } },
            PasteShortcutAction.Emacs => this with { Emacs = Emacs with { Shortcut = shortcut } },
            PasteShortcutAction.Gvim => this with { Gvim = Gvim with { Shortcut = shortcut } },
            PasteShortcutAction.GenericTerminal => this with { GenericTerminal = GenericTerminal with { Shortcut = shortcut } },
            PasteShortcutAction.Custom => this with { Custom = NormalizeCustom(Custom with { Shortcut = shortcut }) },
            _ => this,
        };
    }

    /// <summary>
    /// 修改预置槽位的粘贴方式。自定义槽位的方式由两侧是否都已设置决定，不能直接指定，调用时原样返回。
    /// </summary>
    public PasteShortcutBindings SetMethod(PasteShortcutAction action, PasteMethod method)
    {
        return action switch
        {
            PasteShortcutAction.Enter => this with { Enter = Enter with { Method = method } },
            PasteShortcutAction.ControlShiftV => this with { ControlShiftV = ControlShiftV with { Method = method } },
            PasteShortcutAction.OpenCode => this with { OpenCode = OpenCode with { Method = method } },
            PasteShortcutAction.Emacs => this with { Emacs = Emacs with { Method = method } },
            PasteShortcutAction.Gvim => this with { Gvim = Gvim with { Method = method } },
            PasteShortcutAction.GenericTerminal => this with { GenericTerminal = GenericTerminal with { Method = method } },
            _ => this,
        };
    }

    public PasteShortcutBindings ClearShortcut(PasteShortcutAction action)
    {
        return SetShortcut(action, null).SetMethod(action, PasteMethod.None);
    }

    public PasteShortcutBinding GetBinding(PasteShortcutAction action)
    {
        return action switch
        {
            PasteShortcutAction.Enter => Enter,
            PasteShortcutAction.ControlShiftV => ControlShiftV,
            PasteShortcutAction.OpenCode => OpenCode,
            PasteShortcutAction.Emacs => Emacs,
            PasteShortcutAction.Gvim => Gvim,
            PasteShortcutAction.GenericTerminal => GenericTerminal,
            PasteShortcutAction.Custom => Custom,
            _ => Custom,
        };
    }

    public bool IsValid()
    {
        var entries = GetEntries().ToArray();
        return IsBindingValid(Enter, true)
            && IsBindingValid(ControlShiftV, false)
            && IsBindingValid(OpenCode, false)
            && IsBindingValid(Emacs, false)
            && IsBindingValid(Gvim, false)
            && IsBindingValid(GenericTerminal, false)
            && IsCustomBindingValid(Custom)
            && entries.Select(entry => entry.Binding.Shortcut)
                .Where(shortcut => shortcut is not null)
                .Distinct()
                .Count() == entries.Count(entry => entry.Binding.Shortcut is not null);
    }

    private static bool IsBindingValid(PasteShortcutBinding binding, bool allowBareEnter)
    {
        if (binding.Shortcut is null)
        {
            return binding.Method == PasteMethod.None;
        }

        return binding.Method != PasteMethod.None
            && binding.Method != PasteMethod.CustomInput
            && IsShortcutValid(binding.Shortcut, allowBareEnter);
    }

    private static bool IsCustomBindingValid(PasteShortcutBinding binding)
    {
        if (binding.Shortcut is not null && !IsShortcutValid(binding.Shortcut, false))
        {
            return false;
        }

        if (binding.CustomInput is not null && !binding.CustomInput.IsValid())
        {
            return false;
        }

        return binding.Method == ResolveCustomMethod(binding);
    }

    private static bool IsShortcutValid(PasteShortcut shortcut, bool allowBareEnter)
    {
        if (!HotkeyValidation.IsVirtualKeyInRange(shortcut.VirtualKey)
            || !HotkeyValidation.AreModifiersDefined(shortcut.Modifiers))
        {
            return false;
        }

        return shortcut.Modifiers != HotkeyModifiers.None
            || (allowBareEnter && shortcut.VirtualKey == 0x0D);
    }

    private static PasteShortcutBinding NormalizeCustom(PasteShortcutBinding binding)
    {
        return binding with { Method = ResolveCustomMethod(binding) };
    }

    private static PasteMethod ResolveCustomMethod(PasteShortcutBinding binding)
    {
        return binding.Shortcut is not null && binding.CustomInput is not null
            ? PasteMethod.CustomInput
            : PasteMethod.None;
    }

    private IEnumerable<(PasteShortcutAction Action, PasteShortcutBinding Binding)> GetEntries()
    {
        yield return (PasteShortcutAction.Enter, Enter);
        yield return (PasteShortcutAction.ControlShiftV, ControlShiftV);
        yield return (PasteShortcutAction.OpenCode, OpenCode);
        yield return (PasteShortcutAction.Emacs, Emacs);
        yield return (PasteShortcutAction.Gvim, Gvim);
        yield return (PasteShortcutAction.GenericTerminal, GenericTerminal);
        if (Custom.Shortcut is not null)
        {
            yield return (PasteShortcutAction.Custom, Custom);
        }
    }
}

public sealed record PasteShortcutBinding(PasteShortcut? Shortcut, PasteMethod Method)
{
    /// <summary>
    /// 仅自定义槽位使用：发给远程的按键或鼠标动作。
    /// </summary>
    public RemoteInput? CustomInput { get; init; }
}

public sealed record PasteShortcut(HotkeyModifiers Modifiers, uint VirtualKey);

public enum RemoteMouseButton
{
    None = 0,
    Middle = 1,
    Right = 2,
}

/// <summary>
/// 发给远程的一次输入：带可选修饰键的键盘按键，或带可选修饰键的鼠标按键，二者只能取其一。
/// </summary>
public sealed record RemoteInput(HotkeyModifiers Modifiers, uint VirtualKey, RemoteMouseButton MouseButton)
{
    private const HotkeyModifiers AllowedModifiers =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;

    public bool IsValid()
    {
        if ((Modifiers & ~AllowedModifiers) != 0 || !Enum.IsDefined(MouseButton))
        {
            return false;
        }

        if (MouseButton != RemoteMouseButton.None)
        {
            return VirtualKey == 0;
        }

        return VirtualKey is >= 0x08 and <= 0xFE
            && VirtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
            && VirtualKey is not (>= 0xA0 and <= 0xA5);
    }
}

public enum PasteMethod
{
    None = -1,
    ShiftMiddleClick = 0,
    EmacsYank = 1,
    GvimRegisterPaste = 2,
    ControlShiftV = 3,
    ControlV = 4,
    ClipboardOnly = 5,
    MiddleClick = 6,
    CustomInput = 7,
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
    All = Alt | Control | Shift | Windows,
}
