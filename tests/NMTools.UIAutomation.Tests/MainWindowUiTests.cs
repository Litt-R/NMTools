using System.IO;
using FlaUI.Core.Input;
using NMTools.UIAutomation.Tests;
using Xunit;

namespace NMTools.UIAutomation.Tests;

public sealed class MainWindowUiTests
{
    [RequiresNmToolsAppFact]
    public void MainWindow_LayoutFits_DragAreasWork_AndCloseButtonHidesToTray()
    {
        using var env = new IsolatedAppEnvironment();
        using var session = env.LaunchMainWindow();
        var initialWindowSettings = File.ReadAllText(env.WindowSettingsPath);
        Assert.InRange(session.Window.BoundingRectangle.Width, 523, 525);
        Assert.InRange(session.Window.BoundingRectangle.Height, 349, 351);
        AssertShortcutCardAndFooterFit(session);

        // 验证品牌区、内容卡片、底部和标题栏右侧空白都能拖动。
        session.DragSurface(startX: 45, startY: 40, deltaX: 80, deltaY: 40);
        session.DragSurface(startX: 250, startY: 150, deltaX: -50, deltaY: 30);
        session.DragSurface(startX: 40, startY: 315, deltaX: 40, deltaY: -35);
        session.DragSurface(startX: 488, startY: 20, deltaX: -25, deltaY: 25);

        session.ClickCloseButton();
        Assert.True(session.WaitUntilHidden(), "The close button did not hide the main window to the tray.");
        IsolatedAppEnvironment.WaitFor(
            () => File.ReadAllText(env.WindowSettingsPath) != initialWindowSettings,
            TimeSpan.FromSeconds(5),
            "The window position was not saved after hiding.");
    }

    private static void AssertShortcutCardAndFooterFit(MainWindowSession session)
    {
        var window = session.Window.BoundingRectangle;
        var title = session.Find("MainWindowShortcutTitle").BoundingRectangle;
        var retryTitle = session.Find("MainWindowRetryShortcutTitle").BoundingRectangle;
        var description = session.Find("MainWindowShortcutDescription").BoundingRectangle;
        var settings = session.Find("MainWindowSettingsButton").BoundingRectangle;
        var hide = session.Find("MainWindowHideButton").BoundingRectangle;

        // StackPanel 等布局容器不会稳定出现在 WPF 的 UI Automation 控件树中，
        // 用标题和说明文本的可访问边界检查卡片是否保持紧凑，而不依赖容器 AutomationId。
        Assert.True(retryTitle.Top > title.Bottom, "The retry shortcut row is not below the card title.");
        Assert.InRange(description.Top - title.Bottom, 0, 140);
        Assert.True(description.Bottom < settings.Top, "The shortcut description overlaps the footer buttons.");
        Assert.True(Math.Abs(settings.Top - hide.Top) < 2, "The footer buttons are not aligned.");
        Assert.True(settings.Bottom <= window.Bottom - 12, "The Settings button is clipped by the window edge.");
        Assert.True(hide.Bottom <= window.Bottom - 12, "The Hide to tray button is clipped by the window edge.");
    }
}
