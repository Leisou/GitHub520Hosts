using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace GitHub520Hosts;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局兜底：任何漏网异常都写入日志，避免静默闪退后无从排查
        DispatcherUnhandledException += (_, args) =>
        {
            HostsManager.WriteLog("dispatcher unhandled: " + args.Exception);
            AddLogToMainWindow("界面异常：" + args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            HostsManager.WriteLog("domain unhandled: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            HostsManager.WriteLog("task unobserved: " + args.Exception);
            args.SetObserved();
        };

        // 示例图尺寸覆盖：--size 800x1100（仅用于生成大/中/小预览）
        var sizeIdx = Array.IndexOf(e.Args, "--size");
        if (sizeIdx >= 0 && sizeIdx + 1 < e.Args.Length)
        {
            var parts = e.Args[sizeIdx + 1].ToLowerInvariant().Split('x');
            if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                GitHub520Hosts.MainWindow.SizeOverride = new Size(w, h);
        }

        // 自检模式：--selftest <hosts 副本路径>，跑完整更新/重置流程并输出结果（不触碰系统 hosts）
        var st = Array.IndexOf(e.Args, "--selftest");
        if (st >= 0 && st + 1 < e.Args.Length)
        {
            RunSelfTest(e.Args[st + 1]);
            Shutdown();
            return;
        }

        // 自检模式：--screenshot <png 路径>，渲染窗口后退出（用于自动化验证界面）
        var idx = Array.IndexOf(e.Args, "--screenshot");
        if (idx >= 0 && idx + 1 < e.Args.Length)
        {
            var win = new MainWindow();
            win.Show();
            win.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(1600);
                try { win.SaveScreenshot(e.Args[idx + 1]); }
                catch (Exception ex) { HostsManager.WriteLog("screenshot failed: " + ex.Message); }
                Shutdown();
            });
            return;
        }

        // UI 操作链自检：走真实按钮处理链，依次执行三项功能，验证完成后不闪退
        var uiTest = Array.IndexOf(e.Args, "--uitest");
        if (uiTest >= 0 && uiTest + 1 < e.Args.Length)
        {
            HostsManager.HostsPath = e.Args[uiTest + 1];
            var testWin = new MainWindow();
            MainWindow = testWin;
            testWin.Show();
            testWin.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(300);
                var report = await testWin.RunUiTestAsync();
                File.WriteAllText(Path.Combine(HostsManager.AppDir, "uitest.result.txt"), report, Encoding.UTF8);
                Shutdown();
            });
            return;
        }

        // 非管理员时自动请求提权重启（用户取消则降级为普通权限运行）
        if (!HostsManager.IsAdmin() && Environment.GetEnvironmentVariable("GH520_NO_ELEVATE") != "1")
        {
            if (RelaunchElevated())
            {
                Shutdown();
                return;
            }
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static void AddLogToMainWindow(string message)
    {
        if (Current is App app && app.MainWindow is MainWindow win)
        {
            try { win.Dispatcher.Invoke(() => win.AppendSystemLog(message)); }
            catch { /* 窗口已关闭时忽略 */ }
        }
    }

    private static bool RelaunchElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = HostsManager.AppDir,
            });
            return true;
        }
        catch
        {
            return false; // 用户取消了 UAC
        }
    }

    private sealed class ListProgress : IProgress<Step>
    {
        public List<Step> Items { get; } = new();
        public void Report(Step value) { lock (Items) Items.Add(value); }
    }

    private static void RunSelfTest(string testHostsPath)
    {
        var sb = new StringBuilder();
        HostsManager.HostsPath = testHostsPath;

        try
        {
            var p1 = new ListProgress();
            var r1 = Task.Run(() => HostsManager.UpdateAsync(p1)).GetAwaiter().GetResult();
            sb.AppendLine($"UPDATE ok={r1.Ok} error={r1.Error}");
            lock (p1.Items) foreach (var s in p1.Items) sb.AppendLine($"  [{s.Level}] {s.Message}");

            var size1 = File.Exists(testHostsPath) ? new FileInfo(testHostsPath).Length : -1;

            // 连续第二次更新：验证幂等（不得重复堆叠段落）
            var p1b = new ListProgress();
            var r1b = Task.Run(() => HostsManager.UpdateAsync(p1b)).GetAwaiter().GetResult();
            var size2 = File.Exists(testHostsPath) ? new FileInfo(testHostsPath).Length : -1;
            var body = File.Exists(testHostsPath) ? File.ReadAllText(testHostsPath) : "";
            var markers = System.Text.RegularExpressions.Regex.Matches(body, "GitHub520 Host Start").Count;
            var bom = File.ReadAllBytes(testHostsPath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF });
            sb.AppendLine($"IDEMPOTENT ok={r1b.Ok} first={size1} second={size2} equal={size1 == size2} markers={markers} bom={bom}");

            var p2 = new ListProgress();
            var r2 = Task.Run(() => HostsManager.ResetAsync(p2)).GetAwaiter().GetResult();
            sb.AppendLine($"PURGE ok={r2.Ok} error={r2.Error}");
            lock (p2.Items) foreach (var s in p2.Items) sb.AppendLine($"  [{s.Level}] {s.Message}");

            var final = HostsManager.GetStatus();
            sb.AppendLine($"FINAL size={final.Size} hasBlock={final.HasBlock} count={final.Count}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("SELFTEST-EXCEPTION " + ex);
        }

        File.WriteAllText(testHostsPath + ".result.txt", sb.ToString(), Encoding.UTF8);
    }
}
