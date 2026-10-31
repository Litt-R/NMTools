using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NMTools.Core;

namespace NMTools.App.Services;

public sealed class NoMachineWindowService : ITargetWindowService
{
    private const int ActivationAttempts = 5;
    private const int ActivationRetryDelayMilliseconds = 40;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly string[] NoMachineProcessPrefixes =
    [
        "nxplayer",
        "nxclient",
        "nomachine",
    ];

    public TargetWindow? CaptureForegroundWindow()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        if (!TryQueryProcessIdentity(processId, out var processName, out var processStartTimeUtcTicks))
        {
            return null;
        }

        var className = GetWindowClassName(handle);
        if (!NativeMethods.TryGetCursorPositions(
                handle,
                out var cursorPosition,
                out var bubbleAnchorPosition))
        {
            return null;
        }

        return new TargetWindow(
            handle,
            processId,
            processStartTimeUtcTicks,
            processName,
            className,
            IsNoMachineProcess(processName),
            cursorPosition,
            bubbleAnchorPosition);
    }

    public bool IsValidTarget(TargetWindow target, InputBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        return IsTargetIdentityValid(target)
            && (!options.RequireNoMachineTarget || target.IsNoMachine);
    }

    public bool IsForegroundTarget(TargetWindow target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return IsTargetIdentityValid(target)
            && NativeMethods.GetForegroundWindow() == target.Handle;
    }

    public async Task<bool> TryActivateAsync(
        TargetWindow target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsTargetIdentityValid(target))
        {
            return false;
        }

        // 不调用 ShowWindow(Restore)：NoMachine 全屏时，这会强制退出全屏并恢复普通窗口大小。
        // 目标窗口在触发快捷键前已经是前台窗口，因此这里只做置前和激活，不改变窗口状态。
        for (var attempt = 0; attempt < ActivationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentForeground = NativeMethods.GetForegroundWindow();
            if (currentForeground != target.Handle)
            {
                NativeMethods.BringWindowToTop(target.Handle);
                NativeMethods.SetForegroundWindow(target.Handle);
            }

            if (NativeMethods.GetForegroundWindow() == target.Handle
                && IsTargetIdentityValid(target))
            {
                return true;
            }

            if (attempt + 1 < ActivationAttempts)
            {
                await Task.Delay(ActivationRetryDelayMilliseconds, cancellationToken);
            }
        }

        return false;
    }

    public bool TryGetCursorScreenPosition(TargetWindow target, out ScreenPosition screenPosition)
    {
        ArgumentNullException.ThrowIfNull(target);
        screenPosition = default;

        if (!IsTargetIdentityValid(target)
            || !IsForegroundTarget(target))
        {
            return false;
        }

        var point = new NativeMethods.Point(
            target.CursorPosition.X,
            target.CursorPosition.Y);
        if (!NativeMethods.ClientToScreen(target.Handle, ref point))
        {
            return false;
        }

        screenPosition = new ScreenPosition(point.X, point.Y);
        return true;
    }

    private static bool IsNoMachineProcess(string processName)
    {
        return NoMachineProcessPrefixes.Any(
            knownPrefix => processName.StartsWith(knownPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTargetIdentityValid(TargetWindow target)
    {
        if (!NativeMethods.IsWindow(target.Handle))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(target.Handle, out var processId);
        if (processId != target.ProcessId)
        {
            return false;
        }

        if (!TryQueryProcessIdentity(processId, out var processName, out var startTimeUtcTicks))
        {
            return false;
        }

        return string.Equals(processName, target.ProcessName, StringComparison.OrdinalIgnoreCase)
            && startTimeUtcTicks == target.ProcessStartTimeUtcTicks;
    }

    /// <summary>
    /// 使用轻量 Win32 OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) 查询进程名和启动时间，
    /// 避免 .NET Process.GetProcessById 在热路径上触发模块枚举/性能计数器带来的数十毫秒卡顿。
    /// </summary>
    private static bool TryQueryProcessIdentity(
        uint processId,
        out string processName,
        out long startTimeUtcTicks)
    {
        processName = string.Empty;
        startTimeUtcTicks = 0;

        var processHandle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle != IntPtr.Zero)
        {
            try
            {
                if (NativeMethods.GetProcessTimes(
                        processHandle,
                        out var creationTime,
                        out _,
                        out _,
                        out _))
                {
                    var buffer = new char[1024];
                    var size = buffer.Length;
                    if (NativeMethods.QueryFullProcessImageName(processHandle, 0, buffer, ref size) && size > 0)
                    {
                        var fullPath = new string(buffer, 0, size);
                        processName = Path.GetFileNameWithoutExtension(fullPath);
                        var fileTime = ((long)creationTime.dwHighDateTime << 32) | (uint)creationTime.dwLowDateTime;
                        startTimeUtcTicks = DateTime.FromFileTimeUtc(fileTime).Ticks;
                        return !string.IsNullOrEmpty(processName);
                    }
                }
            }
            finally
            {
                NativeMethods.CloseHandle(processHandle);
            }
        }

        // 兜底：若轻量 Win32 查询失败，回退到 Process.GetProcessById
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            return !string.IsNullOrEmpty(processName);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetWindowClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(
            IntPtr hProcess,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpCreationTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpExitTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            uint dwFlags,
            [Out] char[] lpExeName,
            ref int lpdwSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(
            IntPtr handle,
            [Out] char[] className,
            int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ScreenToClient(IntPtr handle, ref Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr handle, ref Point point);

        public static bool TryGetCursorPositions(
            IntPtr handle,
            out WindowRelativePosition cursorPosition,
            out ScreenPosition bubbleAnchorPosition)
        {
            cursorPosition = default;
            bubbleAnchorPosition = default;
            if (!GetCursorPos(out var point))
            {
                return false;
            }

            bubbleAnchorPosition = new ScreenPosition(point.X, point.Y);
            if (!ScreenToClient(handle, ref point))
            {
                return false;
            }

            cursorPosition = new WindowRelativePosition(point.X, point.Y);
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X;
            public int Y;
        }
    }
}
