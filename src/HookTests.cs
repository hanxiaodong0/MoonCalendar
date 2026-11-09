using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MoonCalendar
{
    public static class HookTests
    {
        public static int Run(string output)
        {
            var results = new List<object>(); int failed = 0;
            Action<string, bool> check = (name, passed) => { results.Add(new { name = name, passed = passed }); if (!passed) failed++; };
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                TaskbarClockIntegration integration = null;
                try
                {
                    int uiThread = Thread.CurrentThread.ManagedThreadId;
                    integration = Create(app.Dispatcher); integration.Start();
                    for (int i = 0; i < 100 && !integration.HookInstalled; i++) await Task.Delay(25);
                    check("入口钩子安装在独立线程", integration.HookInstalled && integration.WorkerThreadId != uiThread);
                    var probe = Task.Run(() => integration.ProbeWorkerAsync(false).GetAwaiter().GetResult());
                    int generation = integration.RenewalCount;
                    Thread.Sleep(1800); // Reproduce a UI stall longer than Windows' hook timeout.
                    check("界面阻塞时入口线程仍响应", probe.IsCompleted && probe.Result == integration.WorkerThreadId);
                    check("界面阻塞时入口仍自动续订", integration.HookInstalled && integration.RenewalCount > generation);
                    await integration.ProbeWorkerAsync(true);
                    generation = integration.RenewalCount;
                    for (int i = 0; i < 100 && integration.RenewalCount == generation; i++) await Task.Delay(25);
                    check("系统静默移除钩子后自动恢复", integration.HookInstalled && integration.RenewalCount > generation);
                    integration.Dispose();
                    check("退出时移除钩子并结束线程", integration.WaitForWorkerExit(3000) && !integration.HookInstalled);
                    integration = null;
                    bool cycles = true;
                    for (int i = 0; i < 5; i++)
                    {
                        using (var cycle = Create(app.Dispatcher))
                        {
                            cycle.Start();
                            for (int n = 0; n < 100 && !cycle.HookInstalled; n++) await Task.Delay(10);
                            cycles &= cycle.HookInstalled;
                            cycle.Dispose(); cycles &= cycle.WaitForWorkerExit(3000) && !cycle.WorkerAlive;
                        }
                    }
                    check("反复启停入口不残留工作线程", cycles);
                }
                catch (Exception error) { failed++; results.Add(new { error = error.ToString() }); }
                finally
                {
                    if (integration != null) integration.Dispose();
                    File.WriteAllText(output, Storage.Json.Serialize(new { passed = failed == 0, total = results.Count, failed = failed, checks = results }), System.Text.Encoding.UTF8);
                    app.Shutdown(failed == 0 ? 0 : 1);
                }
            }), DispatcherPriority.ApplicationIdle);
            return app.Run();
        }
        private static TaskbarClockIntegration Create(Dispatcher dispatcher)
        {
            // Native hooks are installed, but no clock targets are supplied: these
            // tests cannot consume taskbar clicks or overwrite the real app's status.
            return new TaskbarClockIntegration(dispatcher, target => { }, point => { }, false, false, 300);
        }
    }
}
