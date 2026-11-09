using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MoonCalendar
{
    public static class WindowTests
    {
        public static int Run(string output)
        {
            var results = new List<object>();
            int failed = 0;
            Action<string, bool> check = (name, passed) => { results.Add(new { name = name, passed = passed }); if (!passed) failed++; };
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var calendar = new CalendarWindow(true, true);
            bool pinned = calendar.Window.Topmost;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await Task.Delay(250);
                    check("登录启动后实际隐藏", !calendar.Window.IsVisible && !NativeWindows.IsWindowVisible(new WindowInteropHelper(calendar.Window).Handle));
                    var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    var target = new ClockTarget { Left = area.Right - 90, Top = area.Bottom, Right = area.Right - 8, Bottom = area.Bottom + 48 };
                    for (int cycle = 1; cycle <= 5; cycle++)
                    {
                        calendar.Show();
                        await Task.Delay(200);
                        check("第" + cycle + "次唤回主窗口", Visible(calendar.Window));
                        calendar.ToggleClockPopup(target);
                        await Task.Delay(200);
                        check("第" + cycle + "次主窗口转任务栏弹窗", Visible(calendar.Window));
                        calendar.ToggleClockPopup(target);
                        await Task.Delay(100);
                        check("第" + cycle + "次重复点击收起", !calendar.Window.IsVisible);
                        calendar.ToggleClockPopup(target);
                        await Task.Delay(200);
                        check("第" + cycle + "次从隐藏弹出", Visible(calendar.Window));
                        calendar.Window.Hide();
                    }
                    var blocker = new Window { Title = "日历窗口回归测试", Width = 480, Height = 760, Left = area.Right - 500, Top = Math.Max(area.Top, area.Bottom - 780), Topmost = true };
                    try
                    {
                        blocker.Show();
                        await Task.Delay(100);
                        calendar.ToggleClockPopup(target);
                        await Task.Delay(200);
                        NativeWindows.Rect bounds;
                        NativeWindows.GetWindowRect(new WindowInteropHelper(calendar.Window).Handle, out bounds);
                        var center = new NativeWindows.Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
                        check("弹窗在其他窗口上方可见", NativeWindows.RootAt(center) == new WindowInteropHelper(calendar.Window).Handle);
                        calendar.DismissOnOutsideClick(center);
                        check("点击弹窗内部保持显示", Visible(calendar.Window));
                        var dialog = new Window { Title = "日历对话框回归测试", Owner = calendar.Window, Width = 220, Height = 120, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                        try
                        {
                            dialog.Show();
                            await Task.Delay(100);
                            calendar.DismissOnOutsideClick(new NativeWindows.Point { X = bounds.Left - 1, Y = bounds.Top });
                            await Task.Delay(100);
                            check("拥有打开的对话框时不误收起", Visible(calendar.Window) && dialog.IsVisible);
                        }
                        finally { dialog.Close(); }
                        calendar.DismissOnOutsideClick(new NativeWindows.Point { X = bounds.Left - 1, Y = bounds.Top });
                        await Task.Delay(100);
                        check("点击外部收起实际窗口", !calendar.Window.IsVisible && !NativeWindows.IsWindowVisible(new WindowInteropHelper(calendar.Window).Handle));
                        check("收起后恢复用户置顶偏好", calendar.Window.Topmost == pinned);
                        calendar.ToggleClockPopup(target);
                        await Task.Delay(100);
                        calendar.Show();
                        await Task.Delay(100);
                        check("托盘唤回后恢复用户置顶偏好", Visible(calendar.Window) && calendar.Window.Topmost == pinned && calendar.Window.ShowInTaskbar);
                        calendar.Window.Hide();
                        blocker.Activate();
                        calendar.ToggleClockPopup(target);
                        await Task.Delay(100);
                        blocker.Activate();
                        await Task.Delay(100);
                        check("切换到其他窗口后收起", !calendar.Window.IsVisible);
                    }
                    finally { blocker.Close(); }
                    results.Add(new { loadCount = calendar.LoadCount });
                }
                catch (Exception error) { failed++; results.Add(new { error = error.ToString() }); }
                finally
                {
                    calendar.Quit();
                    File.WriteAllText(output, Storage.Json.Serialize(new { passed = failed == 0, failed = failed, results = results }), System.Text.Encoding.UTF8);
                    app.Shutdown(failed == 0 ? 0 : 1);
                }
            }), DispatcherPriority.ApplicationIdle);
            return app.Run(calendar.Window);
        }
        private static bool Visible(Window window)
        {
            return window.IsVisible && NativeWindows.IsWindowVisible(new WindowInteropHelper(window).Handle);
        }
    }
}
