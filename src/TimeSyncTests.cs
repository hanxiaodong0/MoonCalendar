using System;
using System.Xml;

namespace MoonCalendar
{
    public static class TimeSyncTests
    {
        public static void Check(Action<string, bool> check)
        {
            var start = new DateTime(2026, 12, 31, 18, 30, 0, DateTimeKind.Utc);
            string xml = TimeSynchronization.TaskXml(start);
            check("Windows任务计划程序接受校时定义", TimeSynchronization.ValidateTaskDefinition(xml));
            var document = new XmlDocument(); document.LoadXml(xml);
            var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Func<string, string> value = path => document.SelectSingleNode("/t:Task/" + path, ns).InnerText;
            check("校时任务固定24小时无结束期限", XmlConvert.ToTimeSpan(value("t:Triggers/t:TimeTrigger/t:Repetition/t:Interval")) == TimeSpan.FromHours(24) && document.SelectSingleNode("/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:Duration", ns) == null);
            check("校时任务使用UTC边界避免夏令时偏移", value("t:Triggers/t:TimeTrigger/t:StartBoundary") == "2026-12-31T18:30:00Z");
            check("校时任务仅执行Windows校时程序", value("t:Actions/t:Exec/t:Command") == TimeSynchronization.TimeProgram && value("t:Actions/t:Exec/t:Arguments") == "/resync /rediscover" && document.SelectNodes("/t:Task/t:Actions/*", ns).Count == 1);
            check("错过校时补执行且不唤醒电脑", value("t:Settings/t:StartWhenAvailable") == "true" && value("t:Settings/t:WakeToRun") == "false");
            check("校时失败重试有次数限制", value("t:Settings/t:RestartOnFailure/t:Interval") == "PT15M" && value("t:Settings/t:RestartOnFailure/t:Count") == "3");
            check("拒绝覆盖不属于本应用的任务", TimeSynchronization.IsOurTask(xml) && !TimeSynchronization.IsOurTask(xml.Replace("MoonCalendar.TimeSync.v1", "OtherApplication")));
            check("拒绝执行被更改的校时任务", !TimeSynchronization.IsOurTask(xml.Replace("/resync /rediscover", "/unregister")) && !TimeSynchronization.IsOurTask(xml.Replace("PT24H", "PT1M")));
            check("未执行过不显示同步成功", !new TimeSyncStatus { Installed = true, Enabled = true }.Description.Contains("成功"));
            check("同步失败显示真实错误", new TimeSyncStatus { Installed = true, Enabled = true, LastRun = start, LastResult = 1 }.Description.Contains("未成功"));
        }
    }
}
