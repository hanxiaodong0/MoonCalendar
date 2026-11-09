using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("拾光日历")]
[assembly: AssemblyDescription("时间、天气、农历与中国法定节假日桌面日历")]
[assembly: AssemblyCompany("MoonCalendar")]
[assembly: AssemblyProduct("拾光日历")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace MoonCalendar
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (new AssemblyName(e.Name).Name != "lunar") return null;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lunar.dll"))
                {
                    if (stream == null) return null;
                    using (var memory = new MemoryStream()) { stream.CopyTo(memory); return Assembly.Load(memory.ToArray()); }
                }
            };
            try { return Run(args); }
            catch (Exception error)
            {
                Storage.Log(error);
                if (args.Length > 1) File.WriteAllText(args[1], error.ToString());
                else MessageBox.Show("日历暂时无法启动。\n\n" + error.Message + "\n\n详细日志：" + Path.Combine(Storage.DirectoryPath, "errors.log"), "拾光日历", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            if (args.Length > 1 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length > 1 && (args[0] == "--enable-windows-integration" || args[0] == "--disable-windows-integration"))
            {
                bool enable = args[0] == "--enable-windows-integration";
                var settings = Preferences.Load(); settings.ReplaceSystemCalendar = enable;
                if (!Storage.Write("settings.json", settings)) throw new IOException("无法保存集成设置。");
                StartupRegistration.SetEnabled(enable);
                File.WriteAllText(args[1], Storage.Json.Serialize(new { startupEnabled = StartupRegistration.IsEnabled, startupCommand = StartupRegistration.RegisteredCommand, replaceSystemCalendar = settings.ReplaceSystemCalendar }), System.Text.Encoding.UTF8);
                return 0;
            }
            if (args.Length > 1 && args[0] == "--integration-status")
            {
                var targets = System.Threading.Tasks.Task.Run(() => TaskbarClockDetector.Scan()).GetAwaiter().GetResult();
                File.WriteAllText(args[1], Storage.Json.Serialize(new { startupEnabled = StartupRegistration.IsEnabled, startupCommand = StartupRegistration.RegisteredCommand, replaceSystemCalendar = Preferences.Load().ReplaceSystemCalendar, clockTargets = targets }), System.Text.Encoding.UTF8);
                return 0;
            }
            if (args.Length > 1 && args[0] == "--integration-diagnostics")
            {
                var diagnostics = System.Threading.Tasks.Task.Run(() => TaskbarClockDetector.Diagnostics()).GetAwaiter().GetResult();
                File.WriteAllText(args[1], Storage.Json.Serialize(diagnostics), System.Text.Encoding.UTF8);
                return 0;
            }
            if (args.Length > 1 && args[0] == "--network-test")
            {
                var weather = WeatherService.FetchAsync(City.Tianjin).GetAwaiter().GetResult();
                var cities = WeatherService.SearchAsync("Baoding").GetAwaiter().GetResult();
                File.WriteAllText(args[1], Storage.Json.Serialize(new { passed = cities.Count > 0, weather = weather, geocoding = cities }), System.Text.Encoding.UTF8);
                return cities.Count > 0 ? 0 : 1;
            }
            using (var instance = new Mutex(false, "Local\\ShiguangCalendar.Desktop.v1"))
            {
                bool acquired;
                try { acquired = instance.WaitOne(0, false); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    if (!args.Contains("--startup")) IntegrationMessages.ShowExisting();
                    return 0;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Storage.Log(e.Exception);
                    MessageBox.Show("遇到一个问题，请重新打开日历。\n" + e.Exception.Message, "拾光日历", MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown(1);
                };
                var calendar = new CalendarWindow(args.Contains("--startup"));
                if (args.Length > 1 && args[0] == "--capture")
                {
                    var capture = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    capture.Tick += (s, e) =>
                    {
                        capture.Stop();
                        var window = calendar.Window;
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var stream = File.Create(args[1])) encoder.Save(stream);
                        app.Shutdown();
                    };
                    capture.Start();
                }
                int result = app.Run(calendar.Window);
                instance.ReleaseMutex();
                return result;
            }
        }
    }

    public static class SelfTests
    {
        public static int Run(string output)
        {
            var results = new List<object>();
            int failed = 0;
            Action<string, bool> check = (name, passed) => { results.Add(new { name = name, passed = passed }); if (!passed) failed++; };
            Func<int, int, int, DayInfo> day = (y, m, d) => CalendarEngine.Get(new DateTime(y, m, d));
            check("参考图日期：2026-11-09 十月初一", day(2026, 11, 9).LunarMonth == "十月" && day(2026, 11, 9).LunarDay == "初一");
            check("2026 春节", day(2026, 2, 17).Festival.Contains("春节") && day(2026, 2, 17).LunarDay == "初一");
            check("2026 除夕", day(2026, 2, 16).Festival.Contains("除夕"));
            check("2026 中秋", day(2026, 9, 25).Festival.Contains("中秋"));
            check("2026 端午", day(2026, 6, 19).Festival.Contains("端午"));
            check("2026 清明节气", day(2026, 4, 5).SolarTerm == "清明");
            check("2026 立冬节气", day(2026, 11, 7).SolarTerm == "立冬");
            check("2026 小雪节气", day(2026, 11, 22).SolarTerm == "小雪");
            check("传统下元节", day(2026, 11, 23).Festival.Contains("下元"));
            check("2025 闰六月初一", day(2025, 7, 25).LunarMonth == "闰六月" && day(2025, 7, 25).LunarDay == "初一");
            check("2027 春节", day(2027, 2, 6).Festival.Contains("春节"));
            check("元旦与农历独立", day(2026, 1, 1).Festival.Contains("元旦") && day(2026, 1, 1).LunarDay != "初一");
            check("周一为每周第一列", CalendarEngine.MondayOffset(new DateTime(2026, 11, 1)) == 6);
            check("六行月份完整显示", CalendarEngine.WeekRows(new DateTime(2026, 11, 1)) == 6);
            check("原四行月份固定六行", CalendarEngine.WeekRows(new DateTime(2021, 2, 1)) == 6);
            check("原五行月份固定六行", CalendarEngine.WeekRows(new DateTime(2026, 10, 1)) == 6);
            check("农历初一显示月份", day(2026, 11, 9).Label(false) == "十月");
            check("节气独立于纪念日开关", day(2026, 11, 7).Label(false) == "立冬");
            var holidays = new HolidayService();
            foreach (int year in new[] { 2025, 2026, 2027 }) check(year + " 年节假日资源完整", holidays.HasYear(year));
            var newYear = holidays.Get(new DateTime(2026, 1, 1));
            check("元旦放假", newYear != null && newYear.isOffDay && newYear.name == "元旦");
            var workday = holidays.Get(new DateTime(2026, 1, 4));
            check("周日补班", workday != null && !workday.isOffDay);
            var spring = holidays.Get(new DateTime(2026, 2, 23));
            check("春节假期末日", spring != null && spring.isOffDay);
            check("国庆补班", holidays.Get(new DateTime(2026, 10, 10)) != null && !holidays.Get(new DateTime(2026, 10, 10)).isOffDay);
            check("普通周末不伪造法定假日", holidays.Get(new DateTime(2026, 11, 7)) == null);
            check("未来年份无数据不伪造", !holidays.HasYear(2099) && holidays.Get(new DateTime(2099, 1, 1)) == null);
            check("错年份数据拒收", !HolidayService.Valid(new HolidayYear { year = 2025 }, 2026));
            check("无公告数据拒收", !HolidayService.Valid(new HolidayYear { year = 2026, days = new List<HolidayDay>(), papers = new string[0] }, 2026));
            check("天气城市经纬度有效", City.Tianjin.IsValid && !new City { Name = "坏数据", Latitude = Double.NaN }.IsValid);
            check("天气缓存不串城市", !new WeatherCache { CityKey = City.Tianjin.Key, FetchedUtc = DateTime.UtcNow, Data = new WeatherResponse { current = new WeatherCurrent() } }.IsValid(new City { Name = "北京", Latitude = 39.9042, Longitude = 116.4074 }));
            check("天气码雷雨", WeatherService.Description(95) == "雷雨");
            check("支持范围边界", day(1901, 1, 1).LunarDay.Length > 0 && day(2099, 12, 31).LunarDay.Length > 0);
            // Check every month layout in the supported range for lost or overflowing days.
            bool layouts = true;
            for (int y = CalendarEngine.MinYear; y <= CalendarEngine.MaxYear; y++)
                for (int m = 1; m <= 12; m++)
                {
                    var date = new DateTime(y, m, 1);
                    int cells = CalendarEngine.MondayOffset(date) + DateTime.DaysInMonth(y, m);
                    int rows = CalendarEngine.WeekRows(date);
                    var grid = CalendarEngine.GridDates(date);
                    if (rows != 6 || grid.Count != 42 || rows * 7 < cells || grid[0].DayOfWeek != DayOfWeek.Monday || grid[41].DayOfWeek != DayOfWeek.Sunday || grid.Count(d => d.Year == y && d.Month == m) != DateTime.DaysInMonth(y, m)) layouts = false;
                    for (int index = 1; index < grid.Count; index++) if (grid[index] != grid[index - 1].AddDays(1)) layouts = false;
                }
            check("1901–2099 全部 2388 个月布局", layouts);
            var october = CalendarEngine.GridDates(new DateTime(2026, 10, 1));
            check("十月补齐上月日期", october[0] == new DateTime(2026, 9, 28));
            check("十月补齐下月日期", october[41] == new DateTime(2026, 11, 8));
            check("跨年上月日期", CalendarEngine.GridDates(new DateTime(2027, 1, 1))[0] == new DateTime(2026, 12, 28));
            check("自启动路径带空格时正确引用", StartupRegistration.CommandFor(@"G:\My Calendar\MoonCalendar.exe") == "\"G:\\My Calendar\\MoonCalendar.exe\" --startup");
            var target = new ClockTarget { Left = 100, Top = 200, Right = 180, Bottom = 240 };
            check("任务栏时钟边界不吞相邻区域", target.Contains(100, 200) && target.Contains(179, 239) && !target.Contains(180, 220) && !target.Contains(99, 220));
            File.WriteAllText(output, Storage.Json.Serialize(new { passed = failed == 0, total = results.Count, failed = failed, checks = results }), System.Text.Encoding.UTF8);
            return failed == 0 ? 0 : 1;
        }
    }
}
