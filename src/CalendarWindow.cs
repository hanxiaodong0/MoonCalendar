using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MoonCalendar
{
    public sealed class CalendarWindow
    {
        public readonly Window Window;
        private Preferences preferences;
        private readonly HolidayService holidays = new HolidayService();
        private readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        private DateTime month;
        private DateTime selected;
        private DateTime lastToday;
        private WeatherCache weather;
        private int weatherGeneration;
        private bool weatherBusy;
        private bool quitting;
        private bool loaded;
        private readonly Forms.NotifyIcon tray;
        private Window settingsWindow;
        private TaskbarClockIntegration taskbarIntegration;
        private bool popupMode;
        private readonly bool startHidden;
        private readonly TimeZoneInfo chinaZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        private static readonly string[] Weekdays = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };

        public CalendarWindow(bool startHidden = false)
        {
            this.startHidden = startHidden;
            preferences = Preferences.Load();
            Window = (Window)XamlReader.Parse(Storage.ReadResource("MainWindow.xaml"));
            using (var iconStream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                Window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconStream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Window.Topmost = preferences.AlwaysOnTop;
            var now = Now;
            selected = lastToday = now.Date;
            month = new DateTime(now.Year, now.Month, 1);
            weather = Storage.Read<WeatherCache>("weather.json");
            if (weather != null && !weather.IsValid(preferences.City)) weather = null;
            tray = new Forms.NotifyIcon { Text = "拾光日历 · 双击显示", Visible = true };
            using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                tray.Icon = new System.Drawing.Icon(stream);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示日历", null, (s, e) => Window.Dispatcher.Invoke(new Action(Show)));
            menu.Items.Add("回到今天", null, (s, e) => Window.Dispatcher.Invoke(new Action(() => { Show(); GoToday(); })));
            menu.Items.Add("设置", null, (s, e) => Window.Dispatcher.Invoke(new Action(() => { Show(); OpenSettings(); })));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Window.Dispatcher.Invoke(new Action(Quit)));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => Window.Dispatcher.Invoke(new Action(Show));
            DragArea(Find<Grid>("TitleBar"));
            DragArea(Find<StackPanel>("ClockDragArea"));
            Find<Button>("PreviousButton").Click += (s, e) => Navigate(-1);
            Find<Button>("NextButton").Click += (s, e) => Navigate(1);
            Find<Button>("TodayButton").Click += (s, e) => GoToday();
            Find<Button>("MonthPickerButton").Click += (s, e) => OpenMonthPicker();
            Find<Button>("SettingsButton").Click += (s, e) => OpenSettings();
            Find<Button>("WeatherButton").Click += async (s, e) => await RefreshWeather();
            Find<Button>("PinButton").Click += (s, e) => { preferences.AlwaysOnTop = !preferences.AlwaysOnTop; Window.Topmost = preferences.AlwaysOnTop; UpdatePin(); Save(); };
            Find<Button>("MinimizeButton").Click += (s, e) => Window.WindowState = WindowState.Minimized;
            Find<Button>("CloseButton").Click += (s, e) => Window.Close();
            Window.Closing += (s, e) =>
            {
                if (!quitting && preferences.MinimizeToTray)
                {
                    e.Cancel = true;
                    if (settingsWindow != null) settingsWindow.Close();
                    Window.Hide();
                }
                Save();
            };
            Window.Closed += (s, e) => { clock.Stop(); weatherTimer.Stop(); if (taskbarIntegration != null) taskbarIntegration.Dispose(); tray.Dispose(); };
            Window.Deactivated += (s, e) =>
            {
                if (popupMode && settingsWindow == null) Window.Dispatcher.BeginInvoke(new Action(() => { if (popupMode && settingsWindow == null && !Window.IsActive) Window.Hide(); }));
            };
            Window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Left || e.Key == Key.PageUp) { Navigate(-1); e.Handled = true; }
                if (e.Key == Key.Right || e.Key == Key.PageDown) { Navigate(1); e.Handled = true; }
                if (e.Key == Key.Home) { GoToday(); e.Handled = true; }
                if (e.Key == Key.Escape && popupMode) { Window.Hide(); e.Handled = true; }
            };
            clock.Tick += (s, e) => UpdateClock();
            weatherTimer.Tick += async (s, e) => { await RefreshWeather(); await RefreshHolidays(false); };
            Window.Loaded += async (s, e) =>
            {
                loaded = true;
                RestorePosition();
                UpdateClock(); RenderMonth(); RenderWeather(false); UpdatePin();
                IntegrationMessages.Attach(Window, Show);
                ApplyTaskbarIntegration();
                clock.Start(); weatherTimer.Start();
                if (this.startHidden) { Window.ShowInTaskbar = false; Window.Hide(); }
                await Task.WhenAll(RefreshWeather(), RefreshHolidays(false));
            };
        }

        private DateTime Now { get { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, chinaZone); } }
        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private static SolidColorBrush Brush(string value) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value); brush.Freeze(); return brush; }
        private void DragArea(FrameworkElement element)
        {
            element.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject)
                {
                    var source = e.OriginalSource as DependencyObject;
                    while (source != null && source != element)
                    {
                        if (source is Button) return;
                        source = VisualTreeHelper.GetParent(source);
                    }
                }
                try { popupMode = false; Window.ShowInTaskbar = true; Window.DragMove(); } catch (InvalidOperationException) { }
            };
        }
        public void Show() { popupMode = false; Window.ShowInTaskbar = true; Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); }
        private void ApplyTaskbarIntegration()
        {
            if (taskbarIntegration != null) { taskbarIntegration.Dispose(); taskbarIntegration = null; }
            if (preferences.ReplaceSystemCalendar)
            {
                taskbarIntegration = new TaskbarClockIntegration(Window.Dispatcher, ToggleClockPopup);
                taskbarIntegration.Start();
            }
        }
        private void ToggleClockPopup(ClockTarget target)
        {
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            if (popupMode && Window.IsVisible) { Window.Hide(); return; }
            popupMode = true;
            GoToday();
            Window.ShowInTaskbar = false;
            Window.WindowState = WindowState.Normal;
            Window.Show(); Window.UpdateLayout();
            NativeWindows.PlaceAtClock(Window, target);
            Window.Activate();
            NativeWindows.Focus(Window);
        }
        private void Quit() { quitting = true; if (settingsWindow != null) settingsWindow.Close(); Window.Close(); }
        private void Save()
        {
            if (loaded && !popupMode && Window.WindowState == WindowState.Normal) { preferences.Left = Window.Left; preferences.Top = Window.Top; }
            Storage.Write("settings.json", preferences);
        }
        private void RestorePosition()
        {
            if (!preferences.Left.HasValue || !preferences.Top.HasValue) return;
            double left = preferences.Left.Value, top = preferences.Top.Value;
            if (Double.IsNaN(left) || Double.IsInfinity(left) || Double.IsNaN(top) || Double.IsInfinity(top)) return;
            var saved = new Rect(left, top, Window.Width, Window.ActualHeight);
            bool visible = Forms.Screen.AllScreens.Any(screen =>
            {
                var area = screen.WorkingArea;
                var scale = VisualTreeHelper.GetDpi(Window);
                return new Rect(area.Left / scale.DpiScaleX, area.Top / scale.DpiScaleY, area.Width / scale.DpiScaleX, area.Height / scale.DpiScaleY).IntersectsWith(saved);
            });
            if (visible) { Window.Left = left; Window.Top = top; KeepOnScreen(); }
        }
        private void KeepOnScreen()
        {
            var scale = VisualTreeHelper.GetDpi(Window);
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(Window.Left * scale.DpiScaleX), (int)(Window.Top * scale.DpiScaleY))).WorkingArea;
            Window.Left = Math.Max(screen.Left / scale.DpiScaleX, Math.Min(Window.Left, screen.Right / scale.DpiScaleX - Window.ActualWidth));
            Window.Top = Math.Max(screen.Top / scale.DpiScaleY, Math.Min(Window.Top, screen.Bottom / scale.DpiScaleY - Window.ActualHeight));
        }
        private void UpdatePin()
        {
            Find<Button>("PinButton").Foreground = Brush(preferences.AlwaysOnTop ? "#65ACFF" : "#AAB6C7");
            Find<Button>("PinButton").ToolTip = preferences.AlwaysOnTop ? "取消置顶" : "置顶窗口";
            Find<Button>("CloseButton").ToolTip = preferences.MinimizeToTray ? "关闭到托盘；右键托盘可退出" : "退出日历";
        }
        private void UpdateClock()
        {
            var now = Now;
            Find<TextBlock>("ClockText").Text = now.ToString(preferences.Use24Hour ? "HH:mm:ss" : "hh:mm:ss", CultureInfo.InvariantCulture);
            Find<TextBlock>("ClockText").ToolTip = "北京时间（UTC+8）" + (preferences.Use24Hour ? "" : now.Hour < 12 ? " · 上午" : " · 下午");
            Find<TextBlock>("DateText").Text = now.ToString("yyyy年M月d日", CultureInfo.InvariantCulture) + "  " + Weekdays[(int)now.DayOfWeek] + (preferences.Use24Hour ? "" : now.Hour < 12 ? "  上午" : "  下午");
            Find<TextBlock>("LunarText").Text = CalendarEngine.Get(now).FullLunar;
            if (now.Date != lastToday)
            {
                bool wasToday = selected == lastToday;
                lastToday = now.Date;
                if (wasToday) { selected = now.Date; month = new DateTime(now.Year, now.Month, 1); }
                RenderMonth();
            }
        }
        private async void Navigate(int delta)
        {
            DateTime next = month.AddMonths(delta);
            if (next.Year < CalendarEngine.MinYear || next.Year > CalendarEngine.MaxYear) return;
            month = next;
            selected = new DateTime(month.Year, month.Month, Math.Min(selected.Day, DateTime.DaysInMonth(month.Year, month.Month)));
            RenderMonth();
            await RefreshHolidays(false);
        }
        private void GoToday()
        {
            selected = Now.Date;
            month = new DateTime(selected.Year, selected.Month, 1);
            RenderMonth();
        }
        private void RenderMonth()
        {
            Find<TextBlock>("MonthText").Text = month.ToString("yyyy年M月", CultureInfo.InvariantCulture);
            Find<Button>("PreviousButton").IsEnabled = month > new DateTime(CalendarEngine.MinYear, 1, 1);
            Find<Button>("NextButton").IsEnabled = month < new DateTime(CalendarEngine.MaxYear, 12, 1);
            Find<Button>("TodayButton").Visibility = month.Year == Now.Year && month.Month == Now.Month && selected == Now.Date ? Visibility.Collapsed : Visibility.Visible;
            var grid = Find<Grid>("DaysGrid");
            grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear();
            for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            int rows = CalendarEngine.WeekRows(month);
            for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(59) });
            var dates = CalendarEngine.GridDates(month);
            for (int cell = 0; cell < dates.Count; cell++)
            {
                DateTime date = dates[cell];
                bool currentMonth = date.Year == month.Year && date.Month == month.Month;
                DayInfo info = CalendarEngine.Get(date);
                HolidayDay holiday = holidays.Get(date);
                bool today = currentMonth && date == Now.Date;
                bool isSelected = date == selected;
                string numberColor = currentMonth ? "#FFFFFF" : "#718198";
                string label = info.Label(preferences.ShowFestivals);
                bool special = !String.IsNullOrEmpty(info.SolarTerm) || preferences.ShowFestivals && !String.IsNullOrEmpty(info.Festival);
                // Keep the label short in the cell; the full text remains available in the tooltip and footer.
                var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1), IsHitTestVisible = false };
                content.Children.Add(new TextBlock { Text = date.Day.ToString(), FontFamily = new FontFamily("Segoe UI"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush(today ? "#FFFFFF" : numberColor), HorizontalAlignment = HorizontalAlignment.Center });
                content.Children.Add(new TextBlock { Text = label, FontSize = label.Length > 4 ? 9 : 10, MaxWidth = 53, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush(today ? "#FFFFFF" : !currentMonth ? "#68788E" : special ? "#38B5FF" : "#CFD9E7"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
                var circle = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Background = today ? Brush("#3787FF") : Brushes.Transparent, BorderBrush = isSelected && !today ? Brush("#76A9EF") : Brushes.Transparent, BorderThickness = new Thickness(1), Child = content, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var inner = new Grid(); inner.Children.Add(circle);
                if (holiday != null)
                {
                    inner.Children.Add(new TextBlock { Text = holiday.isOffDay ? "休" : "班", FontSize = 8, Foreground = Brush(!currentMonth ? "#68788E" : holiday.isOffDay ? "#66D7B6" : "#FFBD77"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 1, 0), IsHitTestVisible = false });
                }
                var button = new Button { Content = inner, Width = 56, Height = 57, ToolTip = Tooltip(info, holiday), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(button, date.ToString("yyyy年M月d日") + " " + label + (holiday == null ? "" : holiday.isOffDay ? " 放假" : " 补班"));
                DateTime captured = date;
                button.IsEnabled = date.Year >= CalendarEngine.MinYear && date.Year <= CalendarEngine.MaxYear;
                button.Click += async (s, e) =>
                {
                    bool switched = captured.Year != month.Year || captured.Month != month.Month;
                    selected = captured;
                    month = new DateTime(captured.Year, captured.Month, 1);
                    RenderMonth();
                    if (switched) await RefreshHolidays(false);
                };
                Grid.SetRow(button, cell / 7); Grid.SetColumn(button, cell % 7); grid.Children.Add(button);
            }
            RenderSelected();
            if (loaded) Window.Dispatcher.BeginInvoke(new Action(KeepOnScreen), DispatcherPriority.Loaded);
        }
        private string Tooltip(DayInfo info, HolidayDay holiday)
        {
            string text = info.Date.ToString("yyyy年M月d日") + "  " + Weekdays[(int)info.Date.DayOfWeek] + "\n农历 " + info.FullLunar;
            if (!String.IsNullOrEmpty(info.SolarTerm)) text += "\n节气 · " + info.SolarTerm;
            if (!String.IsNullOrEmpty(info.Festival)) text += "\n节日 · " + info.Festival;
            if (holiday != null) text += "\n" + holiday.name + (holiday.isOffDay ? " · 放假" : " · 调休补班");
            else if (!holidays.HasYear(info.Date.Year)) text += "\n该年份暂无已保存的放假调休数据";
            return text;
        }
        private void RenderSelected()
        {
            var info = CalendarEngine.Get(selected);
            var holiday = holidays.Get(selected);
            Find<TextBlock>("SelectedDateText").Text = selected.ToString("M月d日") + "  " + Weekdays[(int)selected.DayOfWeek] + "  ·  " + info.LunarMonth + info.LunarDay;
            var pieces = new List<string>();
            if (holiday != null) pieces.Add(holiday.name + (holiday.isOffDay ? " · 放假" : " · 调休补班"));
            if (!String.IsNullOrEmpty(info.SolarTerm)) pieces.Add(info.SolarTerm);
            if (!String.IsNullOrEmpty(info.Festival)) pieces.Add(info.Festival);
            if (!holidays.HasYear(selected.Year)) pieces.Add("本年放假调休数据暂不可用");
            if (pieces.Count == 0) pieces.Add(info.LunarYear);
            Find<TextBlock>("SelectedInfoText").Text = String.Join("  /  ", pieces);
            Find<TextBlock>("SelectedInfoText").ToolTip = Tooltip(info, holiday);
        }
        private async Task RefreshHolidays(bool force)
        {
            int year = month.Year;
            int[] request = { year - 1, year, year + 1 };
            foreach (int item in request) holidays.Load(item);
            var updated = await Task.WhenAll(request.Select(y => holidays.RefreshAsync(y, force)));
            if (updated.Any(b => b) && Window.IsLoaded) RenderMonth();
        }
        private async Task RefreshWeather()
        {
            if (weatherBusy) return;
            weatherBusy = true;
            int generation = ++weatherGeneration;
            City city = preferences.City;
            Find<TextBlock>("WeatherCaption").Text = city.Name + " · 更新中";
            try
            {
                var result = await WeatherService.FetchAsync(city);
                if (generation != weatherGeneration || city.Key != preferences.City.Key) return;
                weather = result;
                Storage.Write("weather.json", result);
                RenderWeather(false);
            }
            catch
            {
                if (generation == weatherGeneration) RenderWeather(true);
            }
            finally { if (generation == weatherGeneration) weatherBusy = false; }
        }
        private void RenderWeather(bool failed)
        {
            var icon = new WeatherIcon { Code = weather == null ? -1 : weather.Data.current.weather_code, IsDay = weather == null || weather.Data.current.is_day == 1 };
            Find<ContentControl>("WeatherIconHost").Content = icon;
            var button = Find<Button>("WeatherButton");
            if (weather == null)
            {
                Find<TextBlock>("TemperatureText").Text = "--°C";
                Find<TextBlock>("WeatherCaption").Text = preferences.City.Name + (failed ? " · 暂无天气" : " · 正在更新");
                button.ToolTip = "天气暂不可用，点击重试。时间、农历及已保存的节假日仍可离线使用。\n天气：Open-Meteo（CC BY 4.0）";
                return;
            }
            bool stale = failed || DateTime.UtcNow - weather.FetchedUtc > TimeSpan.FromMinutes(60);
            var current = weather.Data.current;
            Find<TextBlock>("TemperatureText").Text = Math.Round(current.temperature_2m).ToString("0", CultureInfo.InvariantCulture) + "°C";
            Find<TextBlock>("WeatherCaption").Text = preferences.City.Name + " · " + (stale ? "缓存" : WeatherService.Description(current.weather_code));
            string detail = preferences.City.Name + " · " + WeatherService.Description(current.weather_code) + "\n体感 " + current.apparent_temperature.ToString("0") + "°C  ·  湿度 " + current.relative_humidity_2m + "%\n风速 " + current.wind_speed_10m.ToString("0.#") + " km/h";
            var daily = weather.Data.daily;
            if (daily != null && daily.temperature_2m_min != null && daily.temperature_2m_max != null && daily.temperature_2m_min.Length > 0 && daily.temperature_2m_max.Length > 0)
                detail += "\n今日 " + daily.temperature_2m_min[0].ToString("0") + " ~ " + daily.temperature_2m_max[0].ToString("0") + "°C";
            detail += "\n数据时间 " + current.time.Replace("T", " ") + "（北京时间）";
            detail += "\n获取于 " + TimeZoneInfo.ConvertTimeFromUtc(weather.FetchedUtc, chinaZone).ToString("M月d日 HH:mm") + (stale ? " · 缓存，点击重试" : " · 点击刷新");
            button.ToolTip = detail + "\n天气：Open-Meteo（CC BY 4.0）";
        }

        private Window Dialog(string title, double width, double height)
        {
            var dialog = new Window { Title = title, Width = width, Height = height, Owner = Window, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Brush("#243143"), Foreground = Brush("#E8EDF5"), FontFamily = Window.FontFamily, FontSize = 12, ShowInTaskbar = false };
            dialog.Resources = Window.Resources;
            dialog.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) dialog.Close(); };
            return dialog;
        }
        private void OpenMonthPicker()
        {
            var dialog = Dialog("跳转到月份", 310, 205);
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock { Text = "选择年份和月份", FontSize = 16, Margin = new Thickness(0, 0, 0, 16) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var years = new ComboBox { Width = 135, Height = 30, ItemsSource = Enumerable.Range(CalendarEngine.MinYear, CalendarEngine.MaxYear - CalendarEngine.MinYear + 1), SelectedItem = month.Year };
            var months = new ComboBox { Width = 95, Height = 30, Margin = new Thickness(12, 0, 0, 0), ItemsSource = Enumerable.Range(1, 12), SelectedItem = month.Month };
            row.Children.Add(years); row.Children.Add(months); panel.Children.Add(row);
            var go = new Button { Content = "跳转", Height = 32, Background = Brush("#357FE7"), Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 0), IsDefault = true };
            go.Click += async (s, e) =>
            {
                month = new DateTime((int)years.SelectedItem, (int)months.SelectedItem, 1);
                selected = month; dialog.Close(); RenderMonth(); await RefreshHolidays(false);
            };
            panel.Children.Add(go); dialog.Content = panel; dialog.ShowDialog();
        }

        private void OpenSettings()
        {
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            var dialog = Dialog("拾光日历 · 设置", 380, 690);
            settingsWindow = dialog;
            dialog.Closed += (s, e) => settingsWindow = null;
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
            panel.Children.Add(new TextBlock { Text = "让日历合你的习惯", FontSize = 19, Margin = new Thickness(0, 0, 0, 20) });
            panel.Children.Add(new TextBlock { Text = "天气城市", Foreground = Brush("#9EB0C8"), Margin = new Thickness(0, 0, 0, 8) });
            var cities = City.Presets();
            if (!cities.Any(c => c.Key == preferences.City.Key)) cities.Insert(0, preferences.City);
            var cityBox = new ComboBox { Height = 32, ItemsSource = cities, SelectedItem = cities.First(c => c.Key == preferences.City.Key) };
            AutomationProperties.SetName(cityBox, "天气城市"); panel.Children.Add(cityBox);
            var searchRow = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition()); searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            var query = new TextBox { Height = 30, Padding = new Thickness(6, 5, 6, 3), Background = Brush("#1B2635"), Foreground = Brushes.White, BorderBrush = Brush("#40536B"), ToolTip = "输入其他城市名称，可尝试中文或拼音" };
            AutomationProperties.SetName(query, "搜索其他城市");
            var search = new Button { Content = "搜索", Margin = new Thickness(7, 0, 0, 0), Background = Brush("#364B66") }; Grid.SetColumn(search, 1);
            searchRow.Children.Add(query); searchRow.Children.Add(search); panel.Children.Add(searchRow);
            var message = new TextBlock { Text = "可选常用城市，也可搜索其他城市。", Foreground = Brush("#859AB6"), FontSize = 10, Margin = new Thickness(0, 7, 0, 13), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(message);
            Func<Task> doSearch = async () =>
            {
                if (String.IsNullOrWhiteSpace(query.Text)) { message.Text = "请输入城市名称。"; return; }
                search.IsEnabled = false; message.Text = "正在搜索城市…";
                try
                {
                    var found = await WeatherService.SearchAsync(query.Text);
                    if (found.Count == 0) { message.Text = "没有找到城市，请试试拼音或完整名称。"; return; }
                    cityBox.ItemsSource = found; cityBox.SelectedIndex = 0;
                    message.Text = "找到 " + found.Count + " 个地点，请在下拉框确认后保存。";
                }
                catch { message.Text = "城市搜索暂不可用，可使用上方常用城市。"; }
                finally { search.IsEnabled = true; }
            };
            search.Click += async (s, e) => await doSearch();
            query.KeyDown += async (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; await doSearch(); } };
            var top = Option(panel, "窗口始终置顶", preferences.AlwaysOnTop);
            var trayOption = Option(panel, "关闭窗口时收起到托盘", preferences.MinimizeToTray);
            var festivals = Option(panel, "显示传统节日和纪念日", preferences.ShowFestivals);
            var format = Option(panel, "使用 24 小时制", preferences.Use24Hour);
            var startup = Option(panel, "开机自启动（登录后收起到托盘）", StartupRegistration.IsEnabled);
            var replace = Option(panel, "点击任务栏时间打开拾光日历", preferences.ReplaceSystemCalendar);
            var integrationHint = new TextBlock { Text = taskbarIntegration == null ? "关闭接管或退出程序后，恢复 Windows 日历。" : taskbarIntegration.StatusText, Foreground = Brush("#859AB6"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            panel.Children.Add(integrationHint);
            panel.Children.Add(new Border { Height = 1, Background = Brush("#3A4B61"), Margin = new Thickness(0, 15, 0, 14) });
            var holidayStatus = new TextBlock { Text = "放假调休：" + month.Year + (holidays.HasYear(month.Year) ? " 年数据已就绪" : " 年暂无数据"), Foreground = Brush("#B4C5DA"), FontSize = 11 };
            panel.Children.Add(holidayStatus);
            var refresh = new Button { Content = "更新放假调休数据", HorizontalAlignment = HorizontalAlignment.Left, Foreground = Brush("#72B8FF"), Height = 27, Margin = new Thickness(0, 4, 0, 4) };
            refresh.Click += async (s, e) =>
            {
                refresh.IsEnabled = false; holidayStatus.Text = "正在更新…";
                bool okay = await holidays.RefreshAsync(month.Year, true);
                holidayStatus.Text = okay ? "放假调休：" + month.Year + " 年数据已更新" : holidays.HasYear(month.Year) ? "未能更新，继续使用已保存的数据" : "该年份暂无可用数据，请稍后重试";
                refresh.IsEnabled = true; RenderMonth();
            };
            panel.Children.Add(refresh);
            panel.Children.Add(new TextBlock { Text = "“休 / 班”依据国务院公布的年度安排。\n纪念日和节气不代表放假。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#859AB6"), FontSize = 10, LineHeight = 17 });
            var attribution = new Button { Content = "天气：Open-Meteo · CC BY 4.0  ↗", Foreground = Brush("#87A9D2"), HorizontalAlignment = HorizontalAlignment.Left, Height = 27, FontSize = 10, Margin = new Thickness(0, 9, 0, 0) };
            attribution.Click += (s, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://open-meteo.com/") { UseShellExecute = true });
            panel.Children.Add(attribution);
            var save = new Button { Content = "保存设置", Height = 36, Background = Brush("#357FE7"), Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 0) };
            save.Click += async (s, e) =>
            {
                try { StartupRegistration.SetEnabled(startup.IsChecked == true); }
                catch (Exception error) { message.Text = "无法保存开机自启动：" + error.Message; return; }
                City city = cityBox.SelectedItem as City;
                bool cityChanged = city != null && city.Key != preferences.City.Key;
                if (city != null) preferences.City = city;
                preferences.AlwaysOnTop = top.IsChecked == true;
                preferences.MinimizeToTray = trayOption.IsChecked == true;
                preferences.ShowFestivals = festivals.IsChecked == true;
                preferences.Use24Hour = format.IsChecked == true;
                bool integrationChanged = preferences.ReplaceSystemCalendar != (replace.IsChecked == true);
                preferences.ReplaceSystemCalendar = replace.IsChecked == true;
                Window.Topmost = preferences.AlwaysOnTop;
                Save(); UpdatePin(); UpdateClock(); RenderMonth(); dialog.Close();
                if (integrationChanged) ApplyTaskbarIntegration();
                if (cityChanged)
                {
                    weatherGeneration++; weatherBusy = false; weather = null; RenderWeather(false); await RefreshWeather();
                }
            };
            panel.Children.Add(save);
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dialog.MaxHeight = SystemParameters.WorkArea.Height - 20;
            dialog.ShowDialog();
        }
        private static CheckBox Option(Panel panel, string title, bool value)
        {
            var check = new CheckBox { Content = title, IsChecked = value, Foreground = Brush("#DCE6F4"), Margin = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(check); return check;
        }
    }

    public sealed class WeatherIcon : FrameworkElement
    {
        public int Code { get; set; }
        public bool IsDay { get; set; }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.PushTransform(new ScaleTransform(ActualWidth / 64, ActualHeight / 52));
            if (Code < 0)
            {
                Cloud(dc, 0.45); dc.Pop(); return;
            }
            bool clear = Code == 0;
            if (Code <= 2)
            {
                if (IsDay)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double angle = i * Math.PI / 4;
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(255, 202, 99)), 2), new Point(27 + 17 * Math.Cos(angle), 22 + 17 * Math.Sin(angle)), new Point(27 + 21 * Math.Cos(angle), 22 + 21 * Math.Sin(angle)));
                    }
                    dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(255, 226, 140), Color.FromRgb(255, 174, 67)), null, new Point(27, 22), 13, 13);
                }
                else
                {
                    var moon = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(28, 22), 17, 17), new EllipseGeometry(new Point(36, 16), 16, 16));
                    dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(223, 232, 255)), null, moon);
                }
            }
            if (!clear) Cloud(dc, 1);
            if (Code >= 51 && Code <= 67 || Code >= 80 && Code <= 82 || Code >= 95)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(93, 177, 255)), 2.5);
                for (int i = 0; i < 3; i++) dc.DrawLine(pen, new Point(24 + i * 12, 43), new Point(21 + i * 12, 49));
            }
            if (Code >= 71 && Code <= 77 || Code == 85 || Code == 86)
                for (int i = 0; i < 3; i++) dc.DrawEllipse(Brushes.AliceBlue, null, new Point(22 + i * 13, 47), 2, 2);
            if (Code == 45 || Code == 48)
                for (int i = 0; i < 2; i++) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(157, 183, 207)), 2), new Point(16, 44 + i * 5), new Point(56, 44 + i * 5));
            dc.Pop();
        }
        private static void Cloud(DrawingContext dc, double opacity)
        {
            dc.PushOpacity(opacity);
            Geometry cloud = new EllipseGeometry(new Point(35, 22), 15, 15);
            cloud = new CombinedGeometry(GeometryCombineMode.Union, cloud, new EllipseGeometry(new Point(48, 29), 13, 12));
            cloud = new CombinedGeometry(GeometryCombineMode.Union, cloud, new EllipseGeometry(new Point(23, 31), 12, 11));
            cloud = new CombinedGeometry(GeometryCombineMode.Union, cloud, new RectangleGeometry(new Rect(23, 28, 26, 14)));
            var gradient = new LinearGradientBrush();
            gradient.StartPoint = new Point(0.25, 0); gradient.EndPoint = new Point(0.7, 1);
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(237, 250, 255), 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(167, 214, 237), 0.55));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(104, 164, 206), 1));
            dc.DrawGeometry(gradient, new Pen(new SolidColorBrush(Color.FromArgb(70, 242, 251, 255)), 0.6), cloud);
            dc.Pop();
        }
    }
}
