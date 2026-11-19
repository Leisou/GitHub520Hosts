using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GitHub520Hosts;

public sealed class LogEntry
{
    public string Time { get; init; } = "";
    public string Message { get; init; } = "";
    public Brush Foreground { get; init; } = Brushes.Black;
}

public partial class MainWindow : Window
{
    /// <summary>仅用于生成大/中/小示例图，正常启动时为 null。</summary>
    public static Size? SizeOverride { get; set; }

    private readonly ObservableCollection<LogEntry> _logs = new();
    private bool _busy;
    private TextBlock _busyTitle;
    private string _busyTitleDefault;

    private static readonly Brush InfoBrush = Frozen(0x3A, 0x3A, 0x3A);
    private static readonly Brush OkBrush = Frozen(0x0F, 0x7B, 0x0F);
    private static readonly Brush WarnBrush = Frozen(0x9D, 0x5D, 0x00);
    private static readonly Brush ErrBrush = Frozen(0xC4, 0x2B, 0x1C);
    private static readonly Brush MutedBrush = Frozen(0x8A, 0x8A, 0x8A);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow()
    {
        InitializeComponent();

        // 固定 8:11 比例；示例图可通过 --size 临时覆盖
        if (SizeOverride is Size s)
        {
            Width = s.Width;
            Height = s.Height;
            MinWidth = MaxWidth = s.Width;
            MinHeight = MaxHeight = s.Height;
        }
        else
        {
            // 屏幕过矮时等比缩小，仍保持 8:11；并夹到工作区内，避免极矮屏窗口溢出屏幕
            // 工作区高度 >= 560 时与原先行为完全一致（h = 660 或 wa.Height-40）
            var wa = SystemParameters.WorkArea;
            var h = Math.Min(660, Math.Max(360, wa.Height - 40));
            h = Math.Min(h, wa.Height - 8);
            Width = h * 8.0 / 11.0;
            Height = h;
            MinWidth = MaxWidth = Width;
            MinHeight = MaxHeight = Height;
        }

        LogList.ItemsSource = _logs;
        SourceInitialized += (_, _) => ApplyRoundedCorners();
        Loaded += async (_, _) =>
        {
            HostsManager.WriteLog($"window {ActualWidth:0}x{ActualHeight:0}, dpi {VisualTreeHelper.GetDpi(this).PixelsPerDip:0.00}");
            AddLog(LogLevel.Muted, "数据源：https://raw.hellogithub.com/hosts");
            AddLog(LogLevel.Muted, "所有改动前自动备份到 backup 目录，可随时回滚。");
            AddLog(LogLevel.Info, "就绪。点击上方任一按钮开始操作。");
            await RefreshStatusAsync();
        };
    }

    /* ------------------------------------------------------- 窗口外观 */

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyRoundedCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* 旧系统无此 API，忽略 */ }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /* ----------------------------------------------------------- 状态 */

    private async Task RefreshStatusAsync()
    {
        HostsStatus st;
        try { st = await Task.Run(HostsManager.GetStatus); }
        catch (Exception ex) { AddLog(LogLevel.Error, "读取状态失败：" + ex.Message); return; }

        TxtPath.Text = st.HostsPath;
        TxtPath.ToolTip = st.HostsPath;

        if (st.Error.Length > 0)
        {
            TxtState.Text = "读取失败";
            TxtState.Foreground = ErrBrush;
            StateDot.Fill = ErrBrush;
        }
        else if (st.HasBlock)
        {
            TxtState.Text = $"已写入 {st.Count} 条 GitHub 解析记录";
            TxtState.Foreground = OkBrush;
            StateDot.Fill = OkBrush;
        }
        else
        {
            TxtState.Text = "未写入（hosts 中无 GitHub520 段落）";
            TxtState.Foreground = MutedBrush;
            StateDot.Fill = Brushes.Silver;
        }

        TxtTime.Text = st.HasBlock ? FmtTime(st.UpdateTime) : "—";
        TxtFoot.Text = $"备份 {st.BackupCount} 份 · {DateTime.Now:HH:mm:ss}";

        if (st.IsAdmin)
        {
            Badge.Background = Frozen(0xE8, 0xF4, 0xE8);
            Badge.BorderBrush = Frozen(0xC6, 0xE3, 0xC6);
            BadgeDot.Fill = OkBrush;
            BadgeText.Text = "管理员权限";
            BadgeText.Foreground = OkBrush;
            AlertBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            Badge.Background = Frozen(0xFD, 0xEC, 0xEA);
            Badge.BorderBrush = Frozen(0xF3, 0xC6, 0xC2);
            BadgeDot.Fill = ErrBrush;
            BadgeText.Text = "权限不足";
            BadgeText.Foreground = ErrBrush;
            AlertBar.Visibility = Visibility.Visible;
        }
    }

    private static string FmtTime(string s)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        var m = Regex.Match(s, @"^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}:\d{2})");
        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : s;
    }

    /* ----------------------------------------------------------- 日志 */

    private void AddLog(LogLevel level, string message)
    {
        // 进度回调可能来自线程池，任何日志写入都必须回到 UI 线程
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.Invoke(() => AddLog(level, message)); }
            catch { /* 窗口关闭期间的日志忽略 */ }
            return;
        }

        var brush = level switch
        {
            LogLevel.Ok => OkBrush,
            LogLevel.Warn => WarnBrush,
            LogLevel.Error => ErrBrush,
            LogLevel.Muted => MutedBrush,
            _ => InfoBrush,
        };

        _logs.Add(new LogEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Message = message,
            Foreground = brush,
        });

        while (_logs.Count > 500) _logs.RemoveAt(0);
        Dispatcher.InvokeAsync(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private IProgress<Step> CreateProgress() => new Progress<Step>(s => AddLog(s.Level, s.Message));

    private void OnClearLogClick(object sender, RoutedEventArgs e) => _logs.Clear();

    /* ----------------------------------------------------------- 操作 */

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        AddLog(LogLevel.Info, "开始更新 GitHub520 Hosts…");
        // Progress<T> 在创建时捕获同步上下文，必须在 UI 线程构造，回调才会回到 UI 线程
        var progress = CreateProgress();
        await RunAsync(TxtUpdateTitle, "正在更新…", "hosts 已更新并生效",
            () => Task.Run(() => HostsManager.UpdateAsync(progress)));
    }

    private async void OnFlushClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        AddLog(LogLevel.Info, "刷新 DNS 缓存…");
        var progress = CreateProgress();
        await RunAsync(TxtFlushTitle, "正在刷新…", "DNS 缓存已刷新",
            () => Task.Run(() => HostsManager.FlushAsync(progress)));
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        AddLog(LogLevel.Info, "一键清除 GitHub520 条目…");
        var progress = CreateProgress();
        await RunAsync(TxtResetTitle, "正在清除…", "GitHub520 条目已清除",
            () => Task.Run(() => HostsManager.ResetAsync(progress)));
    }

    private async Task<OpResult> RunAsync(TextBlock title, string busyText, string okText,
        Func<Task<OpResult>> action)
    {
        _busy = true;
        SetBusyUi(true, title, busyText);
        try
        {
            var result = await action();
            if (result.Ok) AddLog(LogLevel.Ok, okText);
            else AddLog(LogLevel.Error, result.Error);
            return result;
        }
        catch (Exception ex)
        {
            var failure = new OpResult { Ok = false, Error = "意外错误：" + ex.Message };
            AddLog(LogLevel.Error, failure.Error);
            return failure;
        }
        finally
        {
            _busy = false;
            SetBusyUi(false, null, null);
            await RefreshStatusAsync();
        }
    }

    private void SetBusyUi(bool busy, TextBlock title, string busyText)
    {
        BtnUpdate.IsEnabled = !busy;
        BtnFlush.IsEnabled = !busy;
        BtnReset.IsEnabled = !busy;

        if (busy && title is not null)
        {
            _busyTitle = title;
            _busyTitleDefault = title.Text;
            title.Text = busyText;
        }
        else if (_busyTitle is not null)
        {
            _busyTitle.Text = _busyTitleDefault;
            _busyTitle = null;
        }
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = HostsManager.AppDir,
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AddLog(LogLevel.Warn, "提权已取消：" + ex.Message);
        }
    }

    /* --------------------------------------------------------- 自检 */

    /// <summary>走真实 UI 操作链依次执行三项功能，用于验证操作后不再闪退。</summary>
    public async Task<string> RunUiTestAsync()
    {
        var sb = new StringBuilder();
        try
        {
            var p1 = CreateProgress();
            var r1 = await RunAsync(TxtUpdateTitle, "正在更新…", "测试：更新完成",
                () => Task.Run(() => HostsManager.UpdateAsync(p1)));
            sb.AppendLine($"UPDATE ok={r1.Ok} error={r1.Error}");

            var p2 = CreateProgress();
            var r2 = await RunAsync(TxtFlushTitle, "正在刷新…", "测试：刷新完成",
                () => Task.Run(() => HostsManager.FlushAsync(p2)));
            sb.AppendLine($"FLUSH ok={r2.Ok} error={r2.Error}");

            var p3 = CreateProgress();
            var r3 = await RunAsync(TxtResetTitle, "正在重置…", "测试：重置完成",
                () => Task.Run(() => HostsManager.ResetAsync(p3)));
            sb.AppendLine($"PURGE ok={r3.Ok} error={r3.Error}");
            sb.AppendLine($"ALIVE yes, logs={_logs.Count}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("UI-TEST-EXCEPTION " + ex);
        }
        return sb.ToString();
    }

    public void AppendSystemLog(string message) => AddLog(LogLevel.Error, message);

    public void SaveScreenshot(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);

        var rtb = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
