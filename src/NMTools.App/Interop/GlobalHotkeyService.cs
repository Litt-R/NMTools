using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NMTools.Core;

namespace NMTools.App.Interop;

/// <summary>
/// 使用 Win32 RegisterHotKey 接收系统级快捷键。
/// </summary>
public sealed class GlobalHotkeyService : IHotkeyService
{
    private const int HotkeyId = 1;
    private const int RetryHotkeyId = 2;
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _source;
    private bool _registered;
    private bool _retryRegistered;
    private bool _disposed;

    public GlobalHotkeyService(HwndSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _source.AddHook(WindowMessageHook);
    }

    public event EventHandler? HotkeyPressed;

    public event EventHandler? RetryHotkeyPressed;

    public bool Register(InputBridgeOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        if (_registered || _retryRegistered)
        {
            throw new InvalidOperationException(
                "快捷键已经注册，请先注销当前快捷键后再注册新的组合键。");
        }

        if (!HotkeyValidation.IsValidGlobalHotkey(options.HotkeyModifiers, options.HotkeyVirtualKey))
        {
            throw new InvalidOperationException(
                "快捷键必须包含至少一个修饰键，并且主按键必须是有效的 Windows 按键。");
        }

        if (!HotkeyValidation.IsValidOptionalGlobalHotkey(options.RetryHotkeyModifiers, options.RetryHotkeyVirtualKey))
        {
            throw new InvalidOperationException(
                "重试粘贴快捷键必须包含至少一个修饰键，并且主按键必须是有效的 Windows 按键。");
        }

        if (options.HasRetryHotkey
            && options.HotkeyModifiers == options.RetryHotkeyModifiers
            && options.HotkeyVirtualKey == options.RetryHotkeyVirtualKey)
        {
            throw new InvalidOperationException(
                "重试粘贴快捷键不能与唤起气泡的触发快捷键相同。");
        }

        var modifiers = (uint)options.HotkeyModifiers;
        if (!NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, options.HotkeyVirtualKey))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            throw new InvalidOperationException(
                $"无法注册全局快捷键（错误 {error.NativeErrorCode}：{error.Message}）。请更换快捷键。",
                error);
        }

        _registered = true;

        if (options.HasRetryHotkey)
        {
            var retryModifiers = (uint)options.RetryHotkeyModifiers;
            if (!NativeMethods.RegisterHotKey(_source.Handle, RetryHotkeyId, retryModifiers, options.RetryHotkeyVirtualKey))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
                _registered = false;
                throw new InvalidOperationException(
                    $"无法注册重试粘贴快捷键（错误 {error.NativeErrorCode}：{error.Message}）。请更换或清空重试粘贴快捷键。",
                    error);
            }

            _retryRegistered = true;
        }

        return true;
    }

    public void Unregister()
    {
        if (_retryRegistered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, RetryHotkeyId);
            _retryRegistered = false;
        }

        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        _source.RemoveHook(WindowMessageHook);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyId)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
            else if (id == RetryHotkeyId)
            {
                RetryHotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
