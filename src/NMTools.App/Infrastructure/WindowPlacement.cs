using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace NMTools.App.Infrastructure;

/// <summary>
/// 计算主窗口显示前的初始位置：保存的位置仍可见时沿用，否则在主显示器工作区居中。
/// 位置必须在窗口句柄创建之前确定；托盘启动会先 EnsureHandle 再延后 Show，
/// 若交给 WPF 的 CenterScreen 或 Loaded 事件处理，窗口会先出现在默认位置再跳到目标位置。
/// </summary>
public static class WindowPlacement
{
    /// <summary>保存的位置至少要有这么多（DIP）落在屏幕内，用户才抓得住标题栏，否则视为不可见。</summary>
    public const double MinVisibleExtent = 40;

    public static Point Resolve(WindowSettings? saved, Size windowSize, Rect primaryWorkArea, Rect virtualScreen)
    {
        if (saved is not null
            && double.IsFinite(saved.Left)
            && double.IsFinite(saved.Top)
            && IsReachable(new Rect(saved.Left, saved.Top, windowSize.Width, windowSize.Height), virtualScreen))
        {
            return new Point(saved.Left, saved.Top);
        }

        return Center(windowSize, primaryWorkArea);
    }

    public static Point Center(Size windowSize, Rect workArea)
    {
        return new Point(
            workArea.Left + (workArea.Width - windowSize.Width) / 2,
            workArea.Top + (workArea.Height - windowSize.Height) / 2);
    }

    private static bool IsReachable(Rect window, Rect virtualScreen)
    {
        var overlap = Rect.Intersect(window, virtualScreen);
        return !overlap.IsEmpty
            && overlap.Width >= Math.Min(MinVisibleExtent, window.Width)
            && overlap.Height >= Math.Min(MinVisibleExtent, window.Height);
    }
}
