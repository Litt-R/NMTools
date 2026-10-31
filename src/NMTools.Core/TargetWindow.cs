namespace NMTools.Core;

/// <summary>
/// 触发快捷键时捕获的本地目标窗口及其进程身份快照。
/// </summary>
public sealed record TargetWindow(
    nint Handle,
    uint ProcessId,
    long ProcessStartTimeUtcTicks,
    string ProcessName,
    string? ClassName,
    bool IsNoMachine,
    WindowRelativePosition CursorPosition,
    ScreenPosition BubbleAnchorPosition);

/// <summary>
/// 触发快捷键时捕获的相对于目标窗口客户区的鼠标坐标。
/// </summary>
public readonly record struct WindowRelativePosition(int X, int Y);

/// <summary>
/// 屏幕坐标系中的鼠标位置。
/// </summary>
public readonly record struct ScreenPosition(int X, int Y);
