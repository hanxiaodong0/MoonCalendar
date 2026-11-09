using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;

namespace MoonCalendar
{
    public sealed class TimeSyncStatus
    {
        public bool Installed { get; set; }
        public bool Enabled { get; set; }
        public bool Running { get; set; }
        public DateTime? LastRun { get; set; }
        public DateTime? NextRun { get; set; }
        public int LastResult { get; set; }
        public string ConfiguredSource { get; set; }
        public string Error { get; set; }
        public string Description
        {
            get
            {
                if (!String.IsNullOrEmpty(Error)) return "无法读取校时状态：" + Error;
                if (!Installed) return "尚未启用系统时间同步。";
                if (!Enabled) return "自动校时任务已停用。";
                string text = "已启用：每 24 小时校准 Windows 系统时间。";
                if (Running) text += "\n正在同步…";
                else if (LastRun.HasValue) text += "\n上次执行：" + LastRun.Value.ToString("M月d日 HH:mm:ss") + (LastResult == 0 ? " · 成功" : " · 未成功（0x" + LastResult.ToString("X8") + "），请检查网络后重试");
                else text += "\n等待首次同步。";
                if (NextRun.HasValue) text += "\n下次执行：" + NextRun.Value.ToString("M月d日 HH:mm:ss");
                return text;
            }
        }
    }

    public static class TimeSynchronization
    {
        public const string TaskName = "MoonCalendar-TimeSync";
        private const string Source = "MoonCalendar.TimeSync.v1";
        private const string Arguments = "/resync /rediscover";
        public static string TimeProgram { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "w32tm.exe"); } }

        // The privileged task runs only a Windows binary, never this user-writable app.
        internal static string TaskXml(DateTime startUtc)
        {
            if (startUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("开始时间必须是 UTC。");
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>拾光日历：每24小时通过Windows时间服务校准系统时间</Description><Source>" + Source + "</Source></RegistrationInfo>" +
                "<Triggers><TimeTrigger><Repetition><Interval>PT24H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>" + startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>" +
                "<Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT2M</ExecutionTimeLimit><RestartOnFailure><Interval>PT15M</Interval><Count>3</Count></RestartOnFailure></Settings>" +
                "<Actions Context=\"System\"><Exec><Command>" + SecurityElement.Escape(TimeProgram) + "</Command><Arguments>" + Arguments + "</Arguments></Exec></Actions></Task>";
        }

        internal static bool IsOurTask(string xml)
        {
            var document = new XmlDocument { XmlResolver = null }; document.LoadXml(xml);
            var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            var source = document.SelectSingleNode("/t:Task/t:RegistrationInfo/t:Source", ns);
            var command = document.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Command", ns);
            var arguments = document.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Arguments", ns);
            var interval = document.SelectSingleNode("/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:Interval", ns);
            var principal = document.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:UserId", ns);
            return source != null && source.InnerText == Source && command != null && String.Equals(command.InnerText, TimeProgram, StringComparison.OrdinalIgnoreCase)
                && arguments != null && arguments.InnerText == Arguments && document.SelectNodes("/t:Task/t:Actions/*", ns).Count == 1
                && principal != null && principal.InnerText == "S-1-5-18" && interval != null && XmlConvert.ToTimeSpan(interval.InnerText) == TimeSpan.FromHours(24);
        }
        internal static bool ValidateTaskDefinition(string xml)
        {
            object service = null, definition = null;
            try
            {
                service = Connect(); definition = ((dynamic)service).NewTask(0);
                ((dynamic)definition).XmlText = xml;
                return IsOurTask((string)((dynamic)definition).XmlText);
            }
            finally { Release(definition); Release(service); }
        }

        public static TimeSyncStatus GetStatus()
        {
            var status = new TimeSyncStatus { ConfiguredSource = ConfiguredSource() };
            object service = null, folder = null, task = null;
            try
            {
                service = Connect(); folder = ((dynamic)service).GetFolder("\\"); task = FindTask(folder);
                if (task == null) return status;
                if (!IsOurTask((string)((dynamic)task).Xml)) throw new InvalidOperationException("同名任务不属于拾光日历。");
                dynamic registered = task;
                status.Installed = true; status.Enabled = registered.Enabled; int state = registered.State; status.Running = state == 2 || state == 4;
                DateTime last = registered.LastRunTime, next = registered.NextRunTime;
                if (last.Year >= 2000) status.LastRun = last;
                if (next.Year >= 2000) status.NextRun = next;
                status.LastResult = registered.LastTaskResult;
            }
            catch (Exception error) { status.Error = error.Message; }
            finally { Release(task); Release(folder); Release(service); }
            return status;
        }

        private static string ConfiguredSource()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"))
            {
                string type = key == null ? null : key.GetValue("Type") as string;
                if (String.Equals(type, "NT5DS", StringComparison.OrdinalIgnoreCase)) return "Windows 域时间源";
                return key == null ? "Windows 时间服务器" : key.GetValue("NtpServer", "Windows 时间服务器").ToString();
            }
        }

        public static Task<TimeSyncStatus> ChangeRegistrationAsync(bool enable)
        {
            return Task.Run(() =>
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                var start = new ProcessStartInfo(StartupRegistration.ExecutablePath, enable ? "--install-time-sync " + sid : "--remove-time-sync") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                try
                {
                    using (var process = Process.Start(start))
                    {
                        if (process == null) throw new IOException("未能打开管理员授权。");
                        process.WaitForExit();
                        if (process.ExitCode != 0) throw new InvalidOperationException("Windows 未能完成校时设置，请查看错误提示后重试。");
                    }
                }
                catch (Win32Exception error) { if (error.NativeErrorCode == 1223) throw new InvalidOperationException("已取消管理员授权，校时设置未更改。"); throw; }
                var status = GetStatus();
                if (status.Error != null || status.Installed != enable || (enable && !status.Enabled)) throw new IOException(status.Error ?? "校时任务保存后校验失败。");
                return status;
            });
        }

        // Called only by the elevated, short-lived setup process, before the app mutex.
        public static void Install(string userSid)
        {
            RequireAdministrator();
            string sid = new SecurityIdentifier(userSid).Value;
            object service = null, folder = null, existing = null, task = null;
            try
            {
                service = Connect(); folder = ((dynamic)service).GetFolder("\\"); existing = FindTask(folder);
                if (existing != null && !IsOurTask((string)((dynamic)existing).Xml)) throw new InvalidOperationException("已有同名任务，无法替换。");
                using (var controller = new ServiceController("W32Time"))
                {
                    RunSystemProgram(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"), "config w32time start= delayed-auto", 15000);
                    if (controller.Status != ServiceControllerStatus.Running) { controller.Start(); controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)); }
                }
                // Preserve Windows' configured NTP server or domain time hierarchy.
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"))
                {
                    string type = key == null ? "" : key.GetValue("Type", "").ToString();
                    string peers = key == null ? "" : key.GetValue("NtpServer", "").ToString();
                    if (type == "NoSync" || (type != "NT5DS" && String.IsNullOrWhiteSpace(peers))) RunSystemProgram(TimeProgram, "/config /manualpeerlist:time.windows.com,0x8 /syncfromflags:manual /update", 15000);
                }
                string sddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;" + sid + ")";
                task = ((dynamic)folder).RegisterTask(TaskName, TaskXml(DateTime.UtcNow.AddHours(24)), 6, "SYSTEM", null, 5, sddl);
                if (!IsOurTask((string)((dynamic)task).Xml)) throw new IOException("校时任务保存后验证失败。");
                object running = ((dynamic)task).Run(null); Release(running);
            }
            finally { Release(task); Release(existing); Release(folder); Release(service); }
        }

        public static void Remove()
        {
            RequireAdministrator();
            object service = null, folder = null, task = null;
            try
            {
                service = Connect(); folder = ((dynamic)service).GetFolder("\\"); task = FindTask(folder);
                if (task == null) return;
                if (!IsOurTask((string)((dynamic)task).Xml)) throw new InvalidOperationException("同名任务不属于拾光日历，无法删除。");
                ((dynamic)folder).DeleteTask(TaskName, 0);
            }
            finally { Release(task); Release(folder); Release(service); }
        }

        public static async Task<TimeSyncStatus> SynchronizeNowAsync()
        {
            await Task.Run(() =>
            {
                object service = null, folder = null, task = null;
                try
                {
                    service = Connect(); folder = ((dynamic)service).GetFolder("\\"); task = FindTask(folder);
                    if (task == null || !IsOurTask((string)((dynamic)task).Xml)) throw new InvalidOperationException("请先启用每 24 小时自动同步。");
                    if (!(bool)((dynamic)task).Enabled) throw new InvalidOperationException("请先启用自动校时任务。");
                    int state = ((dynamic)task).State;
                    if (state != 2 && state != 4) { object running = ((dynamic)task).Run(null); Release(running); }
                }
                finally { Release(task); Release(folder); Release(service); }
            });
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                var status = await Task.Run(() => GetStatus());
                if (status.Error != null) throw new IOException(status.Error);
                if (!status.Running && status.LastRun.HasValue && status.LastResult != 0x00041303) return status;
            }
            return await Task.Run(() => GetStatus());
        }

        public static string QueryWindowsStatus() { return RunSystemProgram(TimeProgram, "/query /status /verbose", 15000); }
        private static string RunSystemProgram(string file, string arguments, int timeout)
        {
            var encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            using (var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = encoding, StandardErrorEncoding = encoding }))
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout)) { process.Kill(); throw new System.TimeoutException("Windows 校时命令超时。"); }
                string text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new IOException(text.Trim());
                return text.Trim();
            }
        }
        private static object Connect() { dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); service.Connect(null, null, null, null); return service; }
        private static object FindTask(object folder)
        {
            try { return ((dynamic)folder).GetTask(TaskName); }
            catch (Exception error) { if (error.HResult == unchecked((int)0x80070002)) return null; throw; }
        }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        private static void RequireAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("设置系统时间同步需要 Windows 管理员授权。");
        }
    }
}
