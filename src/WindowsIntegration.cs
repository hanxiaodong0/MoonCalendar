using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Condition = System.Windows.Automation.Condition;

namespace MoonCalendar
{
    public static class StartupRegistration
    {
        public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "ShiguangCalendar";
        public static string ExecutablePath { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string CommandFor(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Contains("\"") || !Path.IsPathRooted(path)) throw new ArgumentException("自启动需要有效的绝对路径。");
            string command = "\"" + path + "\" --startup";
            if (command.Length > 260) throw new ArgumentException("程序路径过长，请移到较短的路径后重新启用。");
            return command;
        }
        public static string RegisteredCommand
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath)) return key == null ? null : key.GetValue(ValueName) as string; }
        }
        public static bool IsEnabled { get { return String.Equals(RegisteredCommand, CommandFor(ExecutablePath), StringComparison.OrdinalIgnoreCase); } }
        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key == null) throw new IOException("无法打开当前用户的启动项。");
                if (enabled) key.SetValue(ValueName, CommandFor(ExecutablePath), RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
            if (enabled != IsEnabled) throw new IOException("开机自启动写入后校验失败。");
        }
    }

    public sealed class ClockTarget
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public string AutomationId { get; set; }
        public string ClassName { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore]
        public IntPtr TaskbarHandle { get; set; }
        public bool Contains(int x, int y) { return x >= Left && x < Right && y >= Top && y < Bottom; }
        public bool SameBounds(ClockTarget other) { return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom && TaskbarHandle == other.TaskbarHandle; }
    }

    public static class TaskbarClockDetector
    {
        private static readonly string[] ClockIds = { "SystemTray.DateTimeButton", "SystemTray.ClockButton", "ClockButton", "DateTimeButton" };
        public static ClockTarget[] Scan()
        {
            var result = new List<ClockTarget>();
            foreach (var taskbar in NativeWindows.Taskbars())
            {
                // Windows 10 exposes a real clock HWND. Windows 11 exposes the
                // clock through the taskbar's UI Automation provider instead.
                NativeWindows.EnumChildWindows(taskbar, (child, parameter) =>
                {
                    if (NativeWindows.ClassOf(child) == "TrayClockWClass")
                    {
                        NativeWindows.Rect rect;
                        if (NativeWindows.IsWindowVisible(child) && NativeWindows.GetWindowRect(child, out rect)) Add(result, taskbar, rect.Left, rect.Top, rect.Right, rect.Bottom, "", "TrayClockWClass");
                    }
                    return true;
                }, IntPtr.Zero);
                if (result.Any(t => t.TaskbarHandle == taskbar)) continue;
                try
                {
                    var root = AutomationElement.FromHandle(taskbar);
                    var condition = new OrCondition(ClockIds.Select(id => (Condition)new PropertyCondition(AutomationElement.AutomationIdProperty, id))
                        .Concat(new[] { (Condition)new PropertyCondition(AutomationElement.ClassNameProperty, "ClockButton"), new PropertyCondition(AutomationElement.ClassNameProperty, "DateTimeButton"), new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"), new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButton")) }).ToArray());
                    var elements = root.FindAll(TreeScope.Descendants, condition);
                    foreach (AutomationElement element in elements)
                    {
                        var current = element.Current;
                        if (current.IsOffscreen) continue;
                        var r = current.BoundingRectangle;
                        if (!r.IsEmpty) Add(result, taskbar, (int)r.Left, (int)r.Top, (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom), current.AutomationId, current.ClassName);
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }
            return result.ToArray();
        }
        public static object Diagnostics()
        {
            var items = new List<object>();
            foreach (var handle in NativeWindows.Taskbars())
            {
                NativeWindows.Rect bounds; NativeWindows.GetWindowRect(handle, out bounds);
                var elements = new List<object>();
                string error = null;
                try
                {
                    var root = AutomationElement.FromHandle(handle);
                    foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        var current = element.Current;
                        // Only structural properties, never taskbar app titles or notification text.
                        elements.Add(new { id = current.AutomationId, className = current.ClassName, type = current.ControlType.ProgrammaticName, bounds = current.BoundingRectangle.ToString(), offscreen = current.IsOffscreen });
                    }
                }
                catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
                items.Add(new { taskbarClass = NativeWindows.ClassOf(handle), bounds = bounds, elements = elements, error = error });
            }
            return items;
        }
        private static void Add(List<ClockTarget> list, IntPtr taskbar, int left, int top, int right, int bottom, string id, string className)
        {
            if (right <= left || bottom <= top) return;
            NativeWindows.Rect bounds;
            if (!NativeWindows.GetWindowRect(taskbar, out bounds) || left < bounds.Left || right > bounds.Right || top < bounds.Top || bottom > bounds.Bottom) return;
            var target = new ClockTarget { Left = left, Top = top, Right = right, Bottom = bottom, TaskbarHandle = taskbar, AutomationId = id, ClassName = className };
            if (!list.Any(t => t.SameBounds(target))) list.Add(target);
        }
    }

    public sealed class TaskbarClockIntegration : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly Action<ClockTarget> show;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly NativeWindows.MouseProc callback;
        private volatile ClockTarget[] targets = new ClockTarget[0];
        private IntPtr hook;
        private bool suppressRelease;
        private ClockTarget pressed;
        private int interceptedClicks;
        private bool disposed;
        public string StatusText
        {
            get { return hook == IntPtr.Zero ? "任务栏入口暂未接管，可从托盘打开。" : targets.Length == 0 ? "正在识别任务栏时钟；暂可从托盘打开。" : "任务栏时钟已接管；退出程序即恢复系统日历。"; }
        }
        public TaskbarClockIntegration(Dispatcher dispatcher, Action<ClockTarget> show)
        {
            this.dispatcher = dispatcher; this.show = show; callback = OnMouse;
        }
        public void Start()
        {
            hook = NativeWindows.SetWindowsHookEx(14, callback, NativeWindows.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { Storage.Log(new Win32Exception(Marshal.GetLastWin32Error(), "无法接管任务栏时钟")); PublishStatus(); return; }
            var thread = new Thread(() =>
            {
                string previous = null;
                while (!stop.WaitOne(0))
                {
                    try
                    {
                        var found = TaskbarClockDetector.Scan(); targets = found;
                        string signature = String.Join(";", found.Select(t => t.TaskbarHandle.ToInt64() + ":" + t.Left + "," + t.Top + "," + t.Right + "," + t.Bottom));
                        if (signature != previous) { previous = signature; dispatcher.BeginInvoke(new Action(PublishStatus)); }
                    }
                    catch (Exception error) { Storage.Log(error); }
                    if (stop.WaitOne(3000)) break;
                }
            }) { IsBackground = true, Name = "Calendar taskbar clock discovery" };
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        }
        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            // This callback only hit-tests cached rectangles and queues UI work.
            // UI Automation, I/O and rendering never run inside the mouse hook.
            if (code >= 0 && !disposed)
            {
                int kind = message.ToInt32();
                if (kind == 0x0201)
                {
                    var point = ((NativeWindows.MouseData)Marshal.PtrToStructure(data, typeof(NativeWindows.MouseData))).Point;
                    var target = targets.FirstOrDefault(t => t.Contains(point.X, point.Y));
                    if (target != null && NativeWindows.RootAt(point) == target.TaskbarHandle)
                    {
                        suppressRelease = true; pressed = target;
                        return new IntPtr(1);
                    }
                }
                else if (kind == 0x0202 && suppressRelease)
                {
                    suppressRelease = false;
                    var target = pressed; pressed = null;
                    var point = ((NativeWindows.MouseData)Marshal.PtrToStructure(data, typeof(NativeWindows.MouseData))).Point;
                    if (target != null && target.Contains(point.X, point.Y))
                    {
                        interceptedClicks++;
                        dispatcher.BeginInvoke(new Action(() => { if (!disposed) { show(target); PublishStatus(); } }));
                    }
                    return new IntPtr(1);
                }
            }
            return NativeWindows.CallNextHookEx(hook, code, message, data);
        }
        private void PublishStatus()
        {
            if (disposed) return;
            Storage.Write("integration-status.json", new { hookActive = hook != IntPtr.Zero, clockTargets = targets, interceptedClicks = interceptedClicks, status = StatusText, updatedUtc = DateTime.UtcNow });
        }
        public void Dispose()
        {
            disposed = true; stop.Set();
            if (hook != IntPtr.Zero) { NativeWindows.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }

    public static class IntegrationMessages
    {
        private static readonly uint ShowMessage = NativeWindows.RegisterWindowMessage("ShiguangCalendar.Show.v1");
        public static void Attach(Window window, Action show)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source.AddHook((IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if ((uint)message == ShowMessage) { window.Dispatcher.BeginInvoke(show); handled = true; }
                return IntPtr.Zero;
            });
        }
        public static bool ShowExisting()
        {
            IntPtr handle = NativeWindows.FindWindow(null, "拾光日历");
            return handle != IntPtr.Zero && NativeWindows.PostMessage(handle, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static class NativeWindows
    {
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MouseData { public Point Point; public uint MouseInformation; public uint Flags; public uint Time; public IntPtr ExtraInformation; }
        public delegate bool EnumProc(IntPtr window, IntPtr parameter);
        public delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int kind, MouseProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        public static string ClassOf(IntPtr window) { var value = new StringBuilder(256); GetClassName(window, value, value.Capacity); return value.ToString(); }
        public static IntPtr RootAt(Point point) { return GetAncestor(WindowFromPoint(point), 2); }
        public static List<IntPtr> Taskbars()
        {
            var list = new List<IntPtr>();
            EnumWindows((window, parameter) => { string name = ClassOf(window); if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") list.Add(window); return true; }, IntPtr.Zero);
            return list;
        }
        public static void Focus(Window window) { SetForegroundWindow(new WindowInteropHelper(window).Handle); }
        public static void PlaceAtClock(Window window, ClockTarget clock)
        {
            var handle = new WindowInteropHelper(window).Handle;
            Rect bounds; if (!GetWindowRect(handle, out bounds)) return;
            var area = Forms.Screen.FromPoint(new System.Drawing.Point(clock.Left, clock.Top)).WorkingArea;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            int left = Math.Max(area.Left + 8, Math.Min(clock.Right - width, area.Right - width - 8));
            int top = Math.Max(area.Top + 8, area.Bottom - height - 8);
            SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }
    }
}
