using System.Windows;
using NMTools.App.Infrastructure;

namespace NMTools.Core.Tests;

public sealed class WindowPlacementTests
{
    private static readonly Size WindowSize = new(500, 310);
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private static readonly Rect VirtualScreen = new(0, 0, 1920, 1080);

    [Fact]
    public void Resolve_WithoutSavedSettings_CentersInWorkArea()
    {
        var position = WindowPlacement.Resolve(null, WindowSize, WorkArea, VirtualScreen);

        Assert.Equal(new Point(710, 365), position);
    }

    [Fact]
    public void Resolve_CentersInWorkAreaWithOffset()
    {
        var workArea = new Rect(0, 40, 1920, 1040);

        var position = WindowPlacement.Resolve(null, WindowSize, workArea, VirtualScreen);

        Assert.Equal(new Point(710, 405), position);
    }

    [Fact]
    public void Resolve_WithVisibleSavedPosition_UsesSavedPosition()
    {
        var saved = new WindowSettings { Left = 300, Top = 200, Width = 500, Height = 310 };

        var position = WindowPlacement.Resolve(saved, WindowSize, WorkArea, VirtualScreen);

        Assert.Equal(new Point(300, 200), position);
    }

    [Fact]
    public void Resolve_WithSavedPositionOnSecondaryMonitor_UsesSavedPosition()
    {
        var virtualScreen = new Rect(-1920, 0, 3840, 1080);
        var saved = new WindowSettings { Left = -1500, Top = 100 };

        var position = WindowPlacement.Resolve(saved, WindowSize, WorkArea, virtualScreen);

        Assert.Equal(new Point(-1500, 100), position);
    }

    [Theory]
    [InlineData(5000, 100)]
    [InlineData(-5000, 100)]
    [InlineData(100, 5000)]
    [InlineData(100, -5000)]
    public void Resolve_WithOffScreenSavedPosition_FallsBackToCenter(double left, double top)
    {
        var saved = new WindowSettings { Left = left, Top = top };

        var position = WindowPlacement.Resolve(saved, WindowSize, WorkArea, VirtualScreen);

        Assert.Equal(new Point(710, 365), position);
    }

    [Fact]
    public void Resolve_WithOnlySlightlyVisibleSavedPosition_FallsBackToCenter()
    {
        // 只有 10 DIP 落在屏幕内，用户抓不住窗口。
        var saved = new WindowSettings { Left = 1910, Top = 100 };

        var position = WindowPlacement.Resolve(saved, WindowSize, WorkArea, VirtualScreen);

        Assert.Equal(new Point(710, 365), position);
    }

    [Fact]
    public void Resolve_WithSavedPositionAtVisibleThreshold_UsesSavedPosition()
    {
        var saved = new WindowSettings { Left = 1920 - WindowPlacement.MinVisibleExtent, Top = 100 };

        var position = WindowPlacement.Resolve(saved, WindowSize, WorkArea, VirtualScreen);

        Assert.Equal(new Point(saved.Left, 100), position);
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.NegativeInfinity)]
    public void Resolve_WithNonFiniteSavedPosition_FallsBackToCenter(double left, double top)
    {
        var saved = new WindowSettings { Left = left, Top = top };

        var position = WindowPlacement.Resolve(saved, WindowSize, WorkArea, VirtualScreen);

        Assert.Equal(new Point(710, 365), position);
    }
}
