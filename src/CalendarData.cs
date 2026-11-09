using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Lunar;

namespace MoonCalendar
{
    public static class Storage
    {
        public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShiguangCalendar");
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static string ReadResource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        public static T Read<T>(string name) where T : class
        {
            try { return Json.Deserialize<T>(File.ReadAllText(Path.Combine(DirectoryPath, name), Encoding.UTF8)); }
            catch { return null; }
        }
        public static bool Write(string name, object value)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, name);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                return true;
            }
            catch { return false; }
        }
        public static void Log(Exception error)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath, "errors.log"), DateTime.Now.ToString("s") + " " + error + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    public sealed class City
    {
        public string Name { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Region { get; set; }
        public override string ToString() { return Name + (String.IsNullOrEmpty(Region) ? "" : " · " + Region); }
        public bool IsValid { get { return !String.IsNullOrWhiteSpace(Name) && !Double.IsNaN(Latitude) && !Double.IsNaN(Longitude) && Latitude >= -90 && Latitude <= 90 && Longitude >= -180 && Longitude <= 180; } }
        public string Key { get { return Latitude.ToString("F4", CultureInfo.InvariantCulture) + "," + Longitude.ToString("F4", CultureInfo.InvariantCulture); } }
        public static City Tianjin { get { return new City { Name = "天津", Latitude = 39.0842, Longitude = 117.2009, Region = "中国" }; } }
        public static List<City> Presets()
        {
            return new List<City> {
                Tianjin,
                new City { Name="北京",Latitude=39.9042,Longitude=116.4074 },
                new City { Name="上海",Latitude=31.2304,Longitude=121.4737 },
                new City { Name="广州",Latitude=23.1291,Longitude=113.2644 },
                new City { Name="深圳",Latitude=22.5431,Longitude=114.0579 },
                new City { Name="杭州",Latitude=30.2741,Longitude=120.1551 },
                new City { Name="成都",Latitude=30.5728,Longitude=104.0668 },
                new City { Name="重庆",Latitude=29.5630,Longitude=106.5516 },
                new City { Name="武汉",Latitude=30.5928,Longitude=114.3055 },
                new City { Name="南京",Latitude=32.0603,Longitude=118.7969 },
                new City { Name="西安",Latitude=34.3416,Longitude=108.9398 },
                new City { Name="石家庄",Latitude=38.0428,Longitude=114.5149 },
                new City { Name="济南",Latitude=36.6512,Longitude=117.1201 },
                new City { Name="沈阳",Latitude=41.8057,Longitude=123.4315 },
                new City { Name="郑州",Latitude=34.7466,Longitude=113.6254 },
                new City { Name="长沙",Latitude=28.2282,Longitude=112.9388 },
                new City { Name="昆明",Latitude=25.0389,Longitude=102.7183 }
            };
        }
    }

    public sealed class Preferences
    {
        public City City { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool ShowFestivals { get; set; }
        public bool Use24Hour { get; set; }
        public bool ReplaceSystemCalendar { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public Preferences() { City = City.Tianjin; ShowFestivals = true; Use24Hour = true; MinimizeToTray = true; }
        public static Preferences Load()
        {
            var result = Storage.Read<Preferences>("settings.json") ?? new Preferences();
            if (result.City == null || !result.City.IsValid) result.City = City.Tianjin;
            return result;
        }
    }

    public sealed class DayInfo
    {
        public DateTime Date { get; set; }
        public string LunarMonth { get; set; }
        public string LunarDay { get; set; }
        public string LunarYear { get; set; }
        public string Festival { get; set; }
        public string SolarTerm { get; set; }
        public string Label(bool festivals)
        {
            if (!String.IsNullOrEmpty(SolarTerm)) return SolarTerm;
            if (festivals && !String.IsNullOrEmpty(Festival)) return Festival;
            return LunarDay == "初一" ? LunarMonth : LunarDay;
        }
        public string FullLunar { get { return LunarYear + " " + LunarMonth + LunarDay; } }
    }

    public static class CalendarEngine
    {
        public const int MinYear = 1901;
        public const int MaxYear = 2099;
        private static readonly Dictionary<DateTime, DayInfo> Cache = new Dictionary<DateTime, DayInfo>();
        public static DayInfo Get(DateTime date)
        {
            date = date.Date;
            DayInfo info;
            if (Cache.TryGetValue(date, out info)) return info;
            var solar = Solar.FromDate(date);
            var lunar = solar.Lunar;
            // Festivals are observances. Only HolidayService determines a day off or a make-up workday.
            var festivals = lunar.Festivals.Concat(solar.Festivals).ToList();
            festivals.AddRange(lunar.OtherFestivals.Where(f => f == "下元节" || f == "腊八节" || f == "寒食节" || f.Contains("小年")));
            if (date.Month == 11 && date.Day == 1) festivals.Add("万圣节");
            info = new DayInfo {
                Date = date,
                LunarMonth = lunar.MonthInChinese + "月",
                LunarDay = lunar.DayInChinese,
                LunarYear = lunar.YearInGanZhi + "年 · " + lunar.YearShengXiao,
                SolarTerm = lunar.JieQi,
                Festival = festivals.FirstOrDefault() ?? ""
            };
            if (Cache.Count > 900) Cache.Clear();
            Cache[date] = info;
            return info;
        }
        public static int MondayOffset(DateTime month) { return ((int)month.DayOfWeek + 6) % 7; }
        public static int WeekRows(DateTime month) { return 6; }
        public static List<DateTime> GridDates(DateTime month)
        {
            var first = new DateTime(month.Year, month.Month, 1);
            var start = first.AddDays(-MondayOffset(first));
            return Enumerable.Range(0, 42).Select(index => start.AddDays(index)).ToList();
        }
    }

    public sealed class HolidayDay
    {
        public string name { get; set; }
        public string date { get; set; }
        public bool isOffDay { get; set; }
    }
    public sealed class HolidayYear
    {
        public int year { get; set; }
        public string[] papers { get; set; }
        public List<HolidayDay> days { get; set; }
    }
    public sealed class HolidayService
    {
        private readonly Dictionary<int, HolidayYear> years = new Dictionary<int, HolidayYear>();
        private readonly Dictionary<int, DateTime> checkedAt = new Dictionary<int, DateTime>();
        private readonly HashSet<int> inFlight = new HashSet<int>();
        public HolidayService()
        {
            for (int y = 2025; y <= 2027; y++) Load(y);
        }
        public static bool Valid(HolidayYear data, int year)
        {
            if (data == null || data.year != year || data.days == null || data.days.Count == 0 || data.days.Count > 200 || data.papers == null || data.papers.Length == 0) return false;
            if (!data.papers.All(p => { Uri uri; return Uri.TryCreate(p, UriKind.Absolute, out uri) && uri.Scheme == "https" && (uri.Host == "www.gov.cn" || uri.Host.EndsWith(".gov.cn", StringComparison.OrdinalIgnoreCase)); })) return false;
            var dates = new HashSet<string>();
            foreach (var day in data.days)
            {
                DateTime date;
                if (day == null || String.IsNullOrWhiteSpace(day.name) || day.name.Length > 20 || !DateTime.TryParseExact(day.date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || Math.Abs(date.Year - year) > 1 || !dates.Add(day.date)) return false;
            }
            return true;
        }
        public void Load(int year)
        {
            var data = Storage.Read<HolidayYear>("holidays-" + year + ".json");
            if (!Valid(data, year))
            {
                try { data = Storage.Json.Deserialize<HolidayYear>(Storage.ReadResource("Holidays." + year + ".json") ?? "null"); } catch { data = null; }
            }
            if (Valid(data, year)) years[year] = data;
        }
        public bool HasYear(int year) { return years.ContainsKey(year); }
        public HolidayDay Get(DateTime date)
        {
            string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var item in years.OrderByDescending(p => p.Key))
            {
                if (Math.Abs(item.Key - date.Year) > 1) continue;
                var found = item.Value.days.FirstOrDefault(d => d.date == key);
                if (found != null) return found;
            }
            return null;
        }
        public async Task<bool> RefreshAsync(int year, bool force)
        {
            if (year < CalendarEngine.MinYear || year > CalendarEngine.MaxYear || inFlight.Contains(year)) return false;
            DateTime last;
            if (!force && checkedAt.TryGetValue(year, out last) && DateTime.UtcNow - last < TimeSpan.FromHours(6)) return false;
            inFlight.Add(year);
            checkedAt[year] = DateTime.UtcNow;
            try
            {
                string json = await Network.Client.GetStringAsync("https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/" + year + ".json");
                var data = Storage.Json.Deserialize<HolidayYear>(json);
                if (!Valid(data, year)) return false;
                years[year] = data;
                Storage.Write("holidays-" + year + ".json", data);
                return true;
            }
            catch { return false; }
            finally { inFlight.Remove(year); }
        }
    }

    public static class Network
    {
        public static readonly HttpClient Client = Create();
        private static HttpClient Create()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            // These JSON responses are small. Request identity encoding to avoid older
            // Framework handlers misreading a zlib-wrapped HTTP deflate response.
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
            if (handler.Proxy != null) handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ShiguangCalendar/1.0");
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
            return client;
        }
    }

    public sealed class WeatherCurrent
    {
        public double temperature_2m { get; set; }
        public double apparent_temperature { get; set; }
        public int relative_humidity_2m { get; set; }
        public int weather_code { get; set; }
        public int is_day { get; set; }
        public double wind_speed_10m { get; set; }
        public string time { get; set; }
    }
    public sealed class WeatherDaily
    {
        public double[] temperature_2m_max { get; set; }
        public double[] temperature_2m_min { get; set; }
    }
    public sealed class WeatherResponse
    {
        public WeatherCurrent current { get; set; }
        public WeatherDaily daily { get; set; }
    }
    public sealed class WeatherCache
    {
        public string CityKey { get; set; }
        public DateTime FetchedUtc { get; set; }
        public WeatherResponse Data { get; set; }
        public bool IsValid(City city) { return CityKey == city.Key && Data != null && Data.current != null && FetchedUtc <= DateTime.UtcNow.AddMinutes(5); }
    }
    public sealed class GeoResult
    {
        public string name { get; set; }
        public string admin1 { get; set; }
        public string country { get; set; }
        public double latitude { get; set; }
        public double longitude { get; set; }
    }
    public sealed class GeoResponse { public List<GeoResult> results { get; set; } }
    public static class WeatherService
    {
        public static async Task<WeatherCache> FetchAsync(City city)
        {
            string url = String.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=Asia%2FShanghai", city.Latitude, city.Longitude);
            string json = await Network.Client.GetStringAsync(url);
            var data = Storage.Json.Deserialize<WeatherResponse>(json);
            if (data == null || data.current == null || String.IsNullOrEmpty(data.current.time) || data.current.temperature_2m < -100 || data.current.temperature_2m > 70 || Double.IsNaN(data.current.temperature_2m)) throw new InvalidDataException("天气数据不完整");
            return new WeatherCache { CityKey = city.Key, FetchedUtc = DateTime.UtcNow, Data = data };
        }
        public static async Task<List<City>> SearchAsync(string query)
        {
            var presets = City.Presets().Where(c => c.Name == query.Trim().TrimEnd('市')).ToList();
            if (presets.Count > 0) return presets;
            string json = await Network.Client.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query.Trim()) + "&count=8&language=zh&format=json");
            var data = Storage.Json.Deserialize<GeoResponse>(json);
            return data == null || data.results == null ? new List<City>() : data.results.Select(r => new City { Name = r.name, Region = String.Join(" · ", new[] { r.admin1, r.country }.Where(s => !String.IsNullOrEmpty(s)).Distinct()), Latitude = r.latitude, Longitude = r.longitude }).Where(c => c.IsValid).ToList();
        }
        public static string Description(int code)
        {
            if (code == 0) return "晴";
            if (code == 1 || code == 2) return "多云";
            if (code == 3) return "阴";
            if (code == 45 || code == 48) return "雾";
            if (code >= 51 && code <= 57) return "毛毛雨";
            if (code == 61 || code == 80) return "小雨";
            if (code == 63 || code == 81) return "中雨";
            if (code == 65 || code == 82) return "大雨";
            if (code == 66 || code == 67) return "冻雨";
            if (code >= 71 && code <= 77 || code == 85 || code == 86) return "雪";
            if (code >= 95 && code <= 99) return "雷雨";
            return "未知天气";
        }
    }
}
