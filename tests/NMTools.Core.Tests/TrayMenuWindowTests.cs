using System.Windows;
using System.Windows.Threading;
using NMTools.App;
using NMTools.App.Views;
using Drawing = System.Drawing;

namespace NMTools.Core.Tests;

/// <summary>
/// 托盘菜单关闭路径的回归测试。真实缺陷：关闭窗口的过程中窗口又失去激活，
/// <c>OnDeactivated</c> 再次调用 <c>Close()</c>，WPF 抛出“窗口关闭期间无法调用 Close”，
/// 用户看到“NMTools - 界面错误”弹窗。这里在专用 STA 线程创建真实窗口，不触碰用户桌面上的其他窗口。
/// </summary>
public sealed class TrayMenuWindowTests
{
    [Fact]
    public void CloseSafely_CalledTwice_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            window.Show();

            window.CloseSafely();
            window.CloseSafely();
        });
    }

    [Fact]
    public void CloseSafely_AfterWindowIsAlreadyClosing_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            Exception? thrownDuringClosing = null;
            window.Show();

            // 模拟“窗口已经在被别处关闭、关闭过程中又有人要求关闭”：
            // 从 Closing 事件里再要求关闭一次，之前这里 _isClosing 仍为 false，会再次调用 Close() 并抛异常。
            window.Closing += (_, _) =>
            {
                try
                {
                    window.CloseSafely();
                }
                catch (Exception exception)
                {
                    thrownDuringClosing = exception;
                }
            };

            window.Close();

            Assert.Null(thrownDuringClosing);
        });
    }

    [Fact]
    public void ClosingTheWindowDirectly_MarksItAsClosing()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            window.Show();

            window.Close();

            // 已关闭的窗口再次请求安全关闭必须是无害的空操作。
            window.CloseSafely();
        });
    }

    private static TrayMenuWindow CreateWindow()
    {
        EnsureApplication();
        return new TrayMenuWindow(
            new Drawing.Point(200, 200),
            () => { },
            () => { },
            () => { },
            () => { });
    }

    private static void EnsureApplication()
    {
        // 窗口 XAML 引用了应用级资源 AppFont 和 TrayMenuButtonStyle。
        // 不能加载 App.xaml：它会实例化 NMTools.App.App，而 WPF 一个进程只允许一个 Application，
        // 并且 App 的启动逻辑会创建托盘图标、注册热键。这里只提供窗口真正用到的两个资源。
        // 每个 STA 线程各自需要一个 Application 之外的资源来源，所以挂在线程的 Application 上。
        var app = System.Windows.Application.Current ?? new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        if (!app.Resources.Contains("AppFont"))
        {
            app.Resources["AppFont"] = new System.Windows.Media.FontFamily("Segoe UI");
            app.Resources["TrayMenuButtonStyle"] = new Style(typeof(System.Windows.Controls.Button));
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA 线程中的测试失败：{failure}");
        }
    }
}
