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
using System.Threading.Tasks;
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
        private readonly Action<NativeWindows.Point> outsideClick;
        private readonly ManualResetEventSlim stop = new ManualResetEventSlim(false);
        private readonly NativeWindows.MouseProc callback;
        private readonly bool diagnostics;
        private readonly bool discoverTargets;
        private readonly int renewalMilliseconds;
        private volatile ClockTarget[] targets = new ClockTarget[0];
        private volatile Dispatcher hookDispatcher;
        private Thread hookThread;
        private Thread discoveryThread;
        private IntPtr hook;
        private bool suppressRelease;
        private ClockTarget pressed;
        private int interceptedClicks;
        private int started;
        private int disposeRequested;
        private int statusPending;
        private int renewals;
        private int hookError;
        private long heartbeatTicks;
        private long lastRenewal;
        private bool Disposed { get { return Volatile.Read(ref disposeRequested) != 0; } }
        internal int WorkerThreadId { get { return hookThread == null ? 0 : hookThread.ManagedThreadId; } }
        internal int RenewalCount { get { return Volatile.Read(ref renewals); } }
        internal bool WorkerAlive { get { return hookThread != null && hookThread.IsAlive; } }
        internal bool HookInstalled { get { return !Disposed && hook != IntPtr.Zero && WorkerAlive; } }
        public string StatusText
        {
            get { return Disposed ? "任务栏入口已停用，Windows 日历已恢复。" : !HookInstalled ? "任务栏入口正在恢复，可从托盘打开。" : targets.Length == 0 ? "正在识别任务栏时钟；暂可从托盘打开。" : "任务栏时钟入口已启用；退出程序即恢复系统日历。"; }
        }
        public TaskbarClockIntegration(Dispatcher dispatcher, Action<ClockTarget> show, Action<NativeWindows.Point> outsideClick) : this(dispatcher, show, outsideClick, true, true, 30000) { }
        internal TaskbarClockIntegration(Dispatcher dispatcher, Action<ClockTarget> show, Action<NativeWindows.Point> outsideClick, bool discoverTargets, bool diagnostics, int renewalMilliseconds)
        {
            this.dispatcher = dispatcher; this.show = show; this.outsideClick = outsideClick; callback = OnMouse;
            this.discoverTargets = discoverTargets; this.diagnostics = diagnostics; this.renewalMilliseconds = renewalMilliseconds;
        }
        public void Start()
        {
            if (Disposed) throw new ObjectDisposedException("TaskbarClockIntegration");
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            hookThread = new Thread(HookLoop) { IsBackground = true, Name = "Calendar mouse hook message loop" };
            hookThread.SetApartmentState(ApartmentState.STA); hookThread.Start();
            if (!discoverTargets) return;
            discoveryThread = new Thread(() =>
            {
                try
                {
                    string previous = null;
                    while (!stop.IsSet)
                    {
                        try
                        {
                            var found = TaskbarClockDetector.Scan(); targets = found;
                            string signature = String.Join(";", found.Select(t => t.TaskbarHandle.ToInt64() + ":" + t.Left + "," + t.Top + "," + t.Right + "," + t.Bottom));
                            if (signature != previous) { previous = signature; RequestRenewal(); QueueStatus(); }
                        }
                        catch (Exception error) { QueueUi(() => Storage.Log(error)); }
                        if (stop.Wait(3000)) break;
                    }
                }
                finally { stop.Dispose(); }
            }) { IsBackground = true, Name = "Calendar taskbar clock discovery" };
            discoveryThread.SetApartmentState(ApartmentState.MTA); discoveryThread.Start();
        }
        private void HookLoop()
        {
            DispatcherTimer watchdog = null;
            try
            {
                hookDispatcher = Dispatcher.CurrentDispatcher;
                if (Disposed) return;
                RenewHook();
                watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Min(3000, Math.Max(100, renewalMilliseconds / 2))) };
                watchdog.Tick += (s, e) =>
                {
                    Interlocked.Exchange(ref heartbeatTicks, DateTime.UtcNow.Ticks);
                    if (suppressRelease && !NativeWindows.LeftButtonDown()) { suppressRelease = false; pressed = null; }
                    if (!suppressRelease && (hook == IntPtr.Zero || (Stopwatch.GetTimestamp() - lastRenewal) * 1000.0 / Stopwatch.Frequency >= renewalMilliseconds)) RenewHook();
                    QueueStatus();
                };
                watchdog.Start();
                if (!Disposed) Dispatcher.Run();
            }
            catch (Exception error) { QueueUi(() => Storage.Log(error)); }
            finally
            {
                if (watchdog != null) watchdog.Stop();
                if (hook != IntPtr.Zero) { NativeWindows.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
                hookDispatcher = null;
                QueueStatus();
            }
        }
        private void RenewHook()
        {
            if (Disposed || suppressRelease) return;
            // Install first, then remove the old registration; no gap between them.
            var replacement = NativeWindows.SetWindowsHookEx(14, callback, NativeWindows.GetModuleHandle(null), 0);
            if (replacement == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (hookError != error) QueueUi(() => Storage.Log(new Win32Exception(error, "无法恢复任务栏时钟入口")));
                Volatile.Write(ref hookError, error); QueueStatus(); return;
            }
            var previous = hook; hook = replacement;
            if (previous != IntPtr.Zero) NativeWindows.UnhookWindowsHookEx(previous);
            Interlocked.Increment(ref renewals); Volatile.Write(ref hookError, 0);
            lastRenewal = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref heartbeatTicks, DateTime.UtcNow.Ticks);
            QueueStatus();
        }
        private void RequestRenewal()
        {
            var worker = hookDispatcher;
            if (worker == null || worker.HasShutdownStarted || Disposed) return;
            try { worker.BeginInvoke(new Action(RenewHook)); } catch (InvalidOperationException) { }
        }
        internal Task<int> ProbeWorkerAsync(bool removeNativeHook)
        {
            var worker = hookDispatcher;
            if (worker == null) throw new InvalidOperationException("入口线程尚未启动。");
            return worker.InvokeAsync(() =>
            {
                if (removeNativeHook && hook != IntPtr.Zero) NativeWindows.UnhookWindowsHookEx(hook); // Deliberately retain the stale handle to test silent removal.
                return Thread.CurrentThread.ManagedThreadId;
            }).Task;
        }
        internal bool WaitForWorkerExit(int milliseconds) { return hookThread == null || hookThread.Join(milliseconds); }
        private void QueueUi(Action action)
        {
            if (Disposed || dispatcher.HasShutdownStarted) return;
            try { dispatcher.BeginInvoke(new Action(() => { if (!Disposed) action(); })); } catch (InvalidOperationException) { }
        }
        private void QueueStatus()
        {
            if (!diagnostics || Disposed || Interlocked.CompareExchange(ref statusPending, 1, 0) != 0) return;
            QueueUi(() => { Interlocked.Exchange(ref statusPending, 0); PublishStatus(); });
        }
        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            // This callback only hit-tests cached rectangles and queues UI work.
            // UI Automation, I/O and rendering never run inside the mouse hook.
            // Installed on the dedicated message loop, never the WPF UI thread.
            if (code >= 0 && !Disposed)
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
                        Interlocked.Increment(ref interceptedClicks);
                        QueueUi(() => { show(target); PublishStatus(); });
                    }
                    return new IntPtr(1);
                }
                if (kind == 0x0201 || kind == 0x0204 || kind == 0x0207 || kind == 0x020B)
                {
                    var point = ((NativeWindows.MouseData)Marshal.PtrToStructure(data, typeof(NativeWindows.MouseData))).Point;
                    QueueUi(() => outsideClick(point));
                }
            }
            return NativeWindows.CallNextHookEx(hook, code, message, data);
        }
        private void PublishStatus()
        {
            if (!diagnostics) return;
            long ticks = Interlocked.Read(ref heartbeatTicks);
            Storage.Write("integration-status.json", new { hookActive = HookInstalled, hookWorkerAlive = WorkerAlive, hookWorkerThreadId = WorkerThreadId, hookRenewals = RenewalCount, hookError = Volatile.Read(ref hookError), hookHeartbeatUtc = ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc), clockTargets = targets, interceptedClicks = Volatile.Read(ref interceptedClicks), status = StatusText, updatedUtc = DateTime.UtcNow });
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeRequested, 1) != 0) return;
            stop.Set();
            var worker = hookDispatcher;
            if (worker != null && !worker.HasShutdownStarted) { try { worker.BeginInvokeShutdown(DispatcherPriority.Send); } catch (InvalidOperationException) { } }
            if (discoveryThread == null) stop.Dispose();
            PublishStatus();
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
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
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
        public static bool LeftButtonDown() { return (GetAsyncKeyState(1) & 0x8000) != 0; }
        public static List<IntPtr> Taskbars()
        {
            var list = new List<IntPtr>();
            EnumWindows((window, parameter) => { string name = ClassOf(window); if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") list.Add(window); return true; }, IntPtr.Zero);
            return list;
        }
        public static bool Focus(Window window) { return SetForegroundWindow(new WindowInteropHelper(window).Handle); }
        public static void Raise(Window window) { SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002); }
        public static object CalendarWindowStatus()
        {
            return WindowStatus(FindWindow(null, "拾光日历"));
        }
        internal static object WindowStatus(IntPtr handle)
        {
            Rect bounds; GetWindowRect(handle, out bounds);
            int cloaked; int cloakResult = DwmGetWindowAttribute(handle, 14, out cloaked, sizeof(int));
            return new { found = handle != IntPtr.Zero, handle = handle.ToInt64(), visible = IsWindowVisible(handle), minimized = IsIconic(handle), foreground = GetForegroundWindow() == handle, cloaked = cloakResult == 0 ? (int?)cloaked : null, bounds = bounds };
        }
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
