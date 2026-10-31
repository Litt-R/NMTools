using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NMTools.App.Interop;

/// <summary>
/// 剪贴板现场采集（只读）。
///
/// 关键约束：这里绝不能调用 OpenClipboard 去"检查"剪贴板。
/// 在写入失败的目标场景下，任何额外的打开动作都会加剧占用、改变时序，
/// 让下一次真正写入更容易失败，从而污染现场。只使用不产生占用的查询 API。
///
/// 局限：若占用者以 NULL 作为 owner 调用 OpenClipboard，GetOpenClipboardWindow 返回 0，
/// 此时无法识别持有者；这是 Win32 的限制，不是本类的缺陷。
/// </summary>
public static class ClipboardDiagnostics
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// 返回当前剪贴板持有者的描述。无窗口持有者时返回 null。
    /// </summary>
    public static string? DescribeClipboardOwner()
    {
        var handle = NativeMethods.GetOpenClipboardWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        var className = GetWindowClassName(handle);
        var processName = GetProcessName(processId);
        return $"ownerProcess={processName} ownerPid={processId} ownerClass={className}";
    }

    /// <summary>
    /// 剪贴板序列号。写入前后对比可以判断写入是否真的改变了剪贴板内容。
    /// </summary>
    public static uint GetSequenceNumber()
    {
        try
        {
            return NativeMethods.GetClipboardSequenceNumber();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 在一次写入尝试前后采集的现场快照，用于写入失败时定位原因。
    /// </summary>
    public static string DescribeWriteAttempt(uint sequenceBefore, uint sequenceAfter, Exception? exception)
    {
        var parts = new List<string>(4)
        {
            $"sequenceBefore={sequenceBefore}",
            $"sequenceAfter={sequenceAfter}",
            $"sequenceChanged={sequenceBefore != sequenceAfter}",
        };

        var owner = DescribeClipboardOwner();
        parts.Add(owner ?? "owner=none");

        if (exception is not null)
        {
            parts.Add($"hr=0x{exception.HResult:X8}");
            parts.Add($"type={exception.GetType().Name}");
        }

        return string.Join(' ', parts);
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return "unknown";
        }

        // 先用轻量 Win32 查询，避免 Process.GetProcessById 在热路径上枚举模块。
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new char[1024];
                var size = buffer.Length;
                if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0)
                {
                    return Path.GetFileNameWithoutExtension(new string(buffer, 0, size));
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            // 进程可能在查询间隙退出，或受权限限制无法访问。
            return "unknown";
        }
    }

    private static string GetWindowClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "unknown";
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetOpenClipboardWindow();

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr handle, [Out] char[] className, int maxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            [Out] char[] exeName,
            ref int size);
    }
}
