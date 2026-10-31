using System.Runtime.InteropServices;
using NMTools.App.Infrastructure;
using NMTools.Core;

namespace NMTools.App.Services;

public sealed class SendInputTextInjectionService : ITextInjectionService
{
    private const ushort ScanCodeLeftShift = 0x2A;
    private const ushort ScanCodeRightShift = 0x36;
    private const ushort ScanCodeLeftControl = 0x1D;
    private const ushort ScanCodeLeftAlt = 0x38;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int ModifierReleaseMaxWaitMilliseconds = 600;
    private const int ModifierReleasePollIntervalMilliseconds = 20;
    private const ushort ScanCodeEscape = 0x01;
    private const ushort ScanCodeQuote = 0x28;
    private const ushort ScanCodeEquals = 0x0D;
    private const ushort ScanCodeP = 0x19;
    private const ushort ScanCodeY = 0x15;
    private const ushort ScanCodeV = 0x2F;
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MapVkToVscEx = 4;
    private const int EventIntervalMilliseconds = 25;

    private readonly ITargetWindowService _targetWindowService;
    private readonly IDiagnosticLog? _log;

    public SendInputTextInjectionService(
        ITargetWindowService targetWindowService,
        IDiagnosticLog? log = null)
    {
        _targetWindowService = targetWindowService
            ?? throw new ArgumentNullException(nameof(targetWindowService));
        _log = log;
    }

    public async Task PasteAsync(
        TargetWindow target,
        ScreenPosition cursorPosition,
        PasteMethod method,
        RemoteInput? customInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureForegroundTarget(target);

        // 全局热键（如 Ctrl+Alt+V 重试快捷键）触发时，用户手指往往还按着 Ctrl/Alt/Shift。
        // 先等待物理修饰键松开并发送释放事件，避免残留修饰键与粘贴动作叠加导致远程收到错误组合键。
        await WaitForModifiersReleasedAsync(target, cancellationToken);
        EnsureForegroundTarget(target);

        if (!NativeMethods.GetCursorPos(out var currentCursor))
        {
            throw new InvalidOperationException("无法读取当前鼠标位置。");
        }

        switch (method)
        {
            case PasteMethod.ShiftMiddleClick:
                await SendMouseClickAsync(
                    target,
                    currentCursor,
                    cursorPosition,
                    new[] { ScanCodeLeftShift },
                    MouseEventMiddleDown,
                    MouseEventMiddleUp,
                    cancellationToken);
                break;
            case PasteMethod.MiddleClick:
                await SendMouseClickAsync(
                    target,
                    currentCursor,
                    cursorPosition,
                    Array.Empty<ushort>(),
                    MouseEventMiddleDown,
                    MouseEventMiddleUp,
                    cancellationToken);
                break;
            case PasteMethod.CustomInput:
                await SendCustomInputAsync(
                    target,
                    currentCursor,
                    cursorPosition,
                    customInput,
                    cancellationToken);
                break;
            case PasteMethod.ControlShiftV:
                await SendKeyChordAsync(
                    target,
                    new ushort[] { ScanCodeLeftControl, ScanCodeLeftShift, ScanCodeV },
                    cancellationToken);
                break;
            case PasteMethod.ControlV:
                await SendKeyChordAsync(
                    target,
                    new ushort[] { ScanCodeLeftControl, ScanCodeV },
                    cancellationToken);
                break;
            case PasteMethod.EmacsYank:
                await SendEmacsYankAsync(target, cancellationToken);
                break;
            case PasteMethod.GvimRegisterPaste:
                await SendGvimRegisterPasteAsync(target, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "不支持的粘贴方式。");
        }
    }

    private async Task SendCustomInputAsync(
        TargetWindow target,
        NativeMethods.Point currentCursor,
        ScreenPosition cursorPosition,
        RemoteInput? input,
        CancellationToken cancellationToken)
    {
        if (input is null || !input.IsValid())
        {
            throw new InvalidOperationException("自定义输入无效，已取消发送。");
        }

        var modifierScanCodes = GetModifierScanCodes(input.Modifiers);
        if (input.MouseButton != RemoteMouseButton.None)
        {
            var (down, up) = input.MouseButton == RemoteMouseButton.Right
                ? (MouseEventRightDown, MouseEventRightUp)
                : (MouseEventMiddleDown, MouseEventMiddleUp);
            await SendMouseClickAsync(
                target,
                currentCursor,
                cursorPosition,
                modifierScanCodes,
                down,
                up,
                cancellationToken);
            return;
        }

        var keyScan = NativeMethods.MapVirtualKey(input.VirtualKey, MapVkToVscEx);
        var extended = (keyScan & 0xFF00) == 0xE000;
        var scanCode = (ushort)(keyScan & 0xFF);
        if (scanCode == 0)
        {
            throw new InvalidOperationException("无法把自定义按键转换为扫描码，已取消发送。");
        }

        var pressed = new List<(ushort ScanCode, bool Extended)>();
        try
        {
            foreach (var modifier in modifierScanCodes)
            {
                await SendKeyboardAsync(target, modifier, keyUp: false, cancellationToken);
                pressed.Add((modifier, false));
            }

            await SendKeyboardAsync(target, scanCode, keyUp: false, cancellationToken, extended);
            pressed.Add((scanCode, extended));
        }
        finally
        {
            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                await SendKeyboardAsync(
                    target,
                    pressed[index].ScanCode,
                    keyUp: true,
                    CancellationToken.None,
                    pressed[index].Extended);
            }
        }
    }

    private static ushort[] GetModifierScanCodes(HotkeyModifiers modifiers)
    {
        var codes = new List<ushort>(3);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) codes.Add(ScanCodeLeftControl);
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) codes.Add(ScanCodeLeftAlt);
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) codes.Add(ScanCodeLeftShift);
        return codes.ToArray();
    }

    private async Task SendMouseClickAsync(
        TargetWindow target,
        NativeMethods.Point currentCursor,
        ScreenPosition cursorPosition,
        IReadOnlyList<ushort> modifierScanCodes,
        uint downFlag,
        uint upFlag,
        CancellationToken cancellationToken)
    {
        EnsureForegroundTarget(target);
        var pressed = new List<ushort>(modifierScanCodes.Count);
        try
        {
            foreach (var modifier in modifierScanCodes)
            {
                await SendKeyboardAsync(target, modifier, keyUp: false, cancellationToken);
                pressed.Add(modifier);
            }

            // 修饰键全部按下后、即将点击鼠标的前一刻再读取当前位置并瞬移鼠标，
            // 缩短鼠标停留在目标位置的时间窗口，避免修饰键间隔期间用户晃动鼠标导致点击偏移。
            EnsureForegroundTarget(target);
            var restoreCursor = currentCursor;
            if (NativeMethods.GetCursorPos(out var latestCursor))
            {
                restoreCursor = latestCursor;
            }

            var cursorMoved = restoreCursor.X != cursorPosition.X || restoreCursor.Y != cursorPosition.Y;
            try
            {
                if (cursorMoved && !NativeMethods.SetCursorPos(cursorPosition.X, cursorPosition.Y))
                {
                    throw new InvalidOperationException("无法恢复触发快捷键时的鼠标位置。");
                }

                await SendMouseAsync(target, downFlag, cancellationToken);
                await SendMouseAsync(target, upFlag, cancellationToken);
            }
            finally
            {
                if (cursorMoved)
                {
                    NativeMethods.SetCursorPos(restoreCursor.X, restoreCursor.Y);
                }
            }
        }
        finally
        {
            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                await SendKeyboardAsync(target, pressed[index], keyUp: true, CancellationToken.None);
            }
        }
    }

    private async Task SendEmacsYankAsync(
        TargetWindow target,
        CancellationToken cancellationToken)
    {
        // Emacs 的 C-y 是 yank：从 kill ring/剪贴板粘贴文本。
        await SendKeyChordAsync(
            target,
            new ushort[] { ScanCodeLeftControl, ScanCodeY },
            cancellationToken);
    }

    private async Task SendGvimRegisterPasteAsync(
        TargetWindow target,
        CancellationToken cancellationToken)
    {
        // gVim 的 "+p 必须在 Normal 模式下发送三个按键：双引号、+、p。
        // 先退出 Insert 模式，否则这些字符会被写入编辑缓冲区。
        await SendKeyAsync(target, ScanCodeEscape, cancellationToken);
        await SendKeyAsync(target, ScanCodeQuote, cancellationToken, shift: true);
        await SendKeyAsync(target, ScanCodeEquals, cancellationToken, shift: true);
        await SendKeyAsync(target, ScanCodeP, cancellationToken);
    }

    private async Task SendKeyAsync(
        TargetWindow target,
        ushort scanCode,
        CancellationToken cancellationToken,
        bool shift = false)
    {
        var shiftPressed = false;
        var keyPressed = false;
        try
        {
            if (shift)
            {
                await SendKeyboardAsync(target, ScanCodeLeftShift, keyUp: false, cancellationToken);
                shiftPressed = true;
            }

            await SendKeyboardAsync(target, scanCode, keyUp: false, cancellationToken);
            keyPressed = true;
        }
        finally
        {
            if (keyPressed)
            {
                await SendKeyboardAsync(target, scanCode, keyUp: true, CancellationToken.None);
            }

            if (shiftPressed)
            {
                await SendKeyboardAsync(target, ScanCodeLeftShift, keyUp: true, CancellationToken.None);
            }
        }
    }

    private async Task SendKeyChordAsync(
        TargetWindow target,
        IReadOnlyList<ushort> scanCodes,
        CancellationToken cancellationToken)
    {
        var pressed = new List<ushort>(scanCodes.Count);
        try
        {
            foreach (var scanCode in scanCodes)
            {
                await SendKeyboardAsync(target, scanCode, keyUp: false, cancellationToken);
                pressed.Add(scanCode);
            }
        }
        finally
        {
            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                await SendKeyboardAsync(target, pressed[index], keyUp: true, CancellationToken.None);
            }
        }
    }

    private Task SendKeyboardAsync(
        TargetWindow target,
        ushort scanCode,
        bool keyUp,
        CancellationToken cancellationToken,
        bool extended = false)
    {
        if (!keyUp)
        {
            EnsureForegroundTarget(target);
        }

        return SendInputAsync(
            new Input
            {
                Type = InputKeyboard,
                Data = new InputData
                {
                    Keyboard = new KeyboardInput
                    {
                        ScanCode = scanCode,
                        Flags = KeyEventScanCode
                            | (keyUp ? KeyEventKeyUp : 0)
                            | (extended ? KeyEventExtended : 0),
                    },
                },
            },
            cancellationToken);
    }

    private Task SendMouseAsync(
        TargetWindow target,
        uint mouseFlags,
        CancellationToken cancellationToken)
    {
        EnsureForegroundTarget(target);
        return SendInputAsync(
            new Input
            {
                Type = InputMouse,
                Data = new InputData
                {
                    Mouse = new MouseInput
                    {
                        Flags = mouseFlags,
                    },
                },
            },
            cancellationToken);
    }

    private void EnsureForegroundTarget(TargetWindow target)
    {
        if (!_targetWindowService.IsForegroundTarget(target))
        {
            throw new InvalidOperationException("目标窗口已不再是前台窗口，已取消输入。");
        }
    }

    private async Task WaitForModifiersReleasedAsync(
        TargetWindow target,
        CancellationToken cancellationToken)
    {
        var waited = 0;
        while (AreAnyModifiersPhysicallyDown() && waited < ModifierReleaseMaxWaitMilliseconds)
        {
            await Task.Delay(ModifierReleasePollIntervalMilliseconds, cancellationToken);
            waited += ModifierReleasePollIntervalMilliseconds;
        }

        // 若超时仍有修饰键处于按下状态，主动发送 KeyUp 清理逻辑状态，避免污染后续按键。
        if (IsKeyPhysicallyDown(VkControl))
        {
            await SendKeyboardAsync(target, ScanCodeLeftControl, keyUp: true, CancellationToken.None);
            await SendKeyboardAsync(target, ScanCodeLeftControl, keyUp: true, CancellationToken.None, extended: true);
        }

        if (IsKeyPhysicallyDown(VkMenu))
        {
            await SendKeyboardAsync(target, ScanCodeLeftAlt, keyUp: true, CancellationToken.None);
            await SendKeyboardAsync(target, ScanCodeLeftAlt, keyUp: true, CancellationToken.None, extended: true);
        }

        if (IsKeyPhysicallyDown(VkShift))
        {
            await SendKeyboardAsync(target, ScanCodeLeftShift, keyUp: true, CancellationToken.None);
            await SendKeyboardAsync(target, ScanCodeRightShift, keyUp: true, CancellationToken.None);
        }
    }

    private static bool AreAnyModifiersPhysicallyDown()
    {
        return IsKeyPhysicallyDown(VkControl)
            || IsKeyPhysicallyDown(VkMenu)
            || IsKeyPhysicallyDown(VkShift)
            || IsKeyPhysicallyDown(VkLWin)
            || IsKeyPhysicallyDown(VkRWin);
    }

    private static bool IsKeyPhysicallyDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private async Task SendInputAsync(Input input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sent = NativeMethods.SendInput(
            inputCount: 1,
            inputs: new[] { input },
            inputSize: Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            var errorCode = Marshal.GetLastWin32Error();
            LogSendInputFailure(errorCode);
            throw new InvalidOperationException(
                $"无法发送远程粘贴动作（SendInput 错误代码：{errorCode}）。");
        }

        // 事件已经发出后不再响应取消，确保后续 finally 一定有机会释放修饰键。
        await Task.Delay(EventIntervalMilliseconds, CancellationToken.None);
    }

    /// <summary>
    /// SendInput 失败最常见的原因是 UIPI（目标窗口完整性级别更高）或桌面锁定时
    /// 输入被系统丢弃。错误码必须落盘，否则线上只能看到一句无用的"发送失败"。
    /// </summary>
    private void LogSendInputFailure(int errorCode)
    {
        var reason = errorCode switch
        {
            0 => "no-error-reported (input was blocked by UIPI or the desktop is locked)",
            5 => "access-denied (target runs at a higher integrity level than NMTools)",
            _ => "unexpected",
        };
        _log?.Error(
            "input.sendInput.failed",
            $"win32={errorCode} reason={reason} isElevated={IsProcessElevated()}");
    }

    /// <summary>
    /// 记录本进程是否提权：提权进程向普通窗口发送输入会被 UIPI 拦截，
    /// 这是"能激活但发不出按键"的常见根因。
    /// </summary>
    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    // INPUT.Data 是一个按平台布局的联合体。x64 Windows 上联合体大小必须是 32 字节。
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputData
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }
    }
}
