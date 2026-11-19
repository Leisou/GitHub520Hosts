using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace GitHub520Hosts;

public enum LogLevel { Info, Ok, Warn, Error, Muted }

public sealed record Step(LogLevel Level, string Message);

public sealed class OpResult
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
}

public sealed class HostsStatus
{
    public string HostsPath { get; init; } = "";
    public bool HasBlock { get; init; }
    public int Count { get; init; }
    public string UpdateTime { get; init; } = "";
    public long Size { get; init; }
    public int BackupCount { get; init; }
    public bool IsAdmin { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>hosts 文件的读写、备份与三项操作。所有方法均不触碰 UI 线程状态。</summary>
public static class HostsManager
{
    public const string RemoteUrl = "https://raw.hellogithub.com/hosts";
    private const string StartMark = "# GitHub520 Host Start";
    private const string EndMark = "# GitHub520 Host End";

    /// <summary>hosts 路径，默认系统路径；仅供自检或特殊环境覆盖。</summary>
    public static string HostsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static readonly string AppDir = AppContext.BaseDirectory;
    public static readonly string BackupDir = Path.Combine(AppDir, "backup");
    public static readonly string LogDir = Path.Combine(AppDir, "logs");
    private static readonly string OriginalBak = Path.Combine(BackupDir, "hosts.original.bak");

    // 优先匹配「Start…End」完整段落；若只有孤立 Start（写入中断/手工编辑残留），
    // 则退而匹配到下一个 Start 之前或文件末尾，避免残留标记导致统计错乱、段落堆叠。
    private static readonly Regex BlockRx = new(
        @"^[ \t]*" + Regex.Escape(StartMark) + @"[\s\S]*?(?:"
        + Regex.Escape(EndMark) + @"[^\r\n]*\r?\n?|(?=^[ \t]*" + Regex.Escape(StartMark) + @"|\z))",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex BlockOnlyRx = new(
        @"^[ \t]*" + Regex.Escape(StartMark) + @"[\s\S]*?" + Regex.Escape(EndMark),
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex UpdateTimeRx = new(@"#\s*Update time:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex EntryRx = new(@"^\s*\d{1,3}(\.\d{1,3}){3}\s+\S*github", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum HostsEnc { Utf8, Latin1 }

    static HostsManager()
    {
        // .NET Core 默认不带 GBK 等代码页，需注册后才能正确解码 ipconfig 输出
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /* ------------------------------------------------------------ 环境 */

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /* -------------------------------------------------------- hosts io */

    private static (string Text, HostsEnc Enc, bool Bom) ReadHosts()
    {
        var bytes = File.ReadAllBytes(HostsPath);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), HostsEnc.Utf8, true);

        try
        {
            var strict = new UTF8Encoding(false, true);
            return (strict.GetString(bytes), HostsEnc.Utf8, false);
        }
        catch (DecoderFallbackException)
        {
            // 含非 UTF-8 字节：按字节透传，绝不破坏原有内容（追加部分为纯 ASCII）
            return (Encoding.Latin1.GetString(bytes), HostsEnc.Latin1, false);
        }
    }

    private static void WriteHosts(string text, HostsEnc enc, bool bom)
    {
        try
        {
            var attrs = File.GetAttributes(HostsPath);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(HostsPath, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* 属性修改失败时由下面的写入报出更明确的错误 */ }

        var body = enc == HostsEnc.Latin1 ? Encoding.Latin1.GetBytes(text) : new UTF8Encoding(false).GetBytes(text);
        byte[] data = bom
            ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray()
            : body;

        try
        {
            File.WriteAllBytes(HostsPath, data);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("没有写入 hosts 的权限，请以管理员身份运行本工具");
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32)
        {
            throw new InvalidOperationException("hosts 文件被其他程序占用，请稍后重试（可临时关闭杀毒软件的 hosts 保护）");
        }
    }

    private static (bool HasBlock, int Count, string UpdateTime) Analyze(string text)
    {
        var m = BlockOnlyRx.Match(text);
        if (!m.Success) return (false, 0, "");

        var count = m.Value
            .Split('\n')
            .Select(l => l.Trim())
            .Count(l => l.Length > 0 && !l.StartsWith('#'));

        var t = UpdateTimeRx.Match(m.Value);
        return (true, count, t.Success ? t.Groups[1].Value.Trim() : "");
    }

    private static string StripBlock(string text) =>
        BlockRx.Replace(text, "").TrimEnd();

    public static HostsStatus GetStatus()
    {
        var admin = IsAdmin();
        try
        {
            var (text, _, _) = ReadHosts();
            var info = Analyze(text);
            return new HostsStatus
            {
                HostsPath = HostsPath,
                HasBlock = info.HasBlock,
                Count = info.Count,
                UpdateTime = info.UpdateTime,
                Size = new FileInfo(HostsPath).Length,
                BackupCount = CountBackups(),
                IsAdmin = admin,
            };
        }
        catch (Exception ex)
        {
            return new HostsStatus { HostsPath = HostsPath, IsAdmin = admin, BackupCount = CountBackups(), Error = ex.Message };
        }
    }

    private static int CountBackups()
    {
        try
        {
            return Directory.Exists(BackupDir)
                ? Directory.GetFiles(BackupDir, "hosts.*").Length
                : 0;
        }
        catch { return 0; }
    }

    private static string MakeBackup(string tag)
    {
        Directory.CreateDirectory(BackupDir);
        if (!File.Exists(OriginalBak))
            File.Copy(HostsPath, OriginalBak, false);

        var target = Path.Combine(BackupDir, $"hosts.{tag}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        File.Copy(HostsPath, target, true);
        return Path.GetFileName(target);
    }

    /* ---------------------------------------------------------- 网络 */

    private static async Task<string> FetchRemoteAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GitHub520Hosts/2.0");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        try
        {
            var text = await http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text) || text.Length < 64)
                throw new InvalidOperationException("服务器返回内容为空或异常");
            return text;
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("请求超时（25 秒），请检查网络后重试");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法连接数据源，请检查网络或代理设置后重试");
        }
    }

    private static string ValidateRemote(string text)
    {
        if (!text.Contains(StartMark, StringComparison.Ordinal)) return $"内容缺少标记「{StartMark}」";
        if (!text.Contains(EndMark, StringComparison.Ordinal)) return $"内容缺少标记「{EndMark}」";

        var hits = text.Split('\n').Count(l => EntryRx.IsMatch(l));
        if (hits < 3) return "内容中没有有效的 GitHub 解析记录，可能源文件异常";
        return "";
    }

    /* ----------------------------------------------------------- dns */

    private static string FlushDns()
    {
        var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo("ipconfig", "/flushdns")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };

        using var p = Process.Start(psi);
        if (p is null) throw new InvalidOperationException("无法启动 ipconfig");
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Replace("\r", "").Trim();
        p.WaitForExit(10000);

        if (p.ExitCode != 0)
        {
            if (output.Contains("提升") || output.Contains("elevation", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("刷新 DNS 需要管理员权限");
            throw new InvalidOperationException($"刷新失败：{output}");
        }
        return output;
    }

    /* --------------------------------------------------------- 操作 */

    public static async Task<OpResult> UpdateAsync(IProgress<Step> log, CancellationToken ct = default)
    {
        try
        {
            log.Report(new Step(LogLevel.Info, "正在读取当前 hosts 文件…"));
            var (curText, enc, bom) = ReadHosts();

            var bak = MakeBackup("update");
            log.Report(new Step(LogLevel.Muted, $"已备份 → backup\\{bak}"));

            log.Report(new Step(LogLevel.Info, $"正在请求 {RemoteUrl} …"));
            var remote = await FetchRemoteAsync().ConfigureAwait(false);
            log.Report(new Step(LogLevel.Ok, $"远程内容获取成功（{remote.Length} 字节）"));

            var problem = ValidateRemote(remote);
            if (problem.Length > 0)
                throw new InvalidOperationException($"内容校验失败：{problem}");

            var block = remote.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim();
            var remoteInfo = Analyze(block);

            var cleaned = StripBlock(curText);
            var finalText = (cleaned.Length > 0 ? cleaned + "\r\n\r\n" : "") + block + "\r\n";

            log.Report(new Step(LogLevel.Info, "正在写入 hosts（替换旧的 GitHub520 段落）…"));
            WriteHosts(finalText, enc, bom);

            var after = Analyze(ReadHosts().Text);
            if (after.Count < 3)
                throw new InvalidOperationException("写入后校验失败，hosts 中未发现有效条目");
            log.Report(new Step(LogLevel.Ok, $"写入完成，共 {after.Count} 条 GitHub 解析记录"));

            log.Report(new Step(LogLevel.Info, "正在刷新 DNS 缓存…"));
            try
            {
                FlushDns();
                log.Report(new Step(LogLevel.Ok, "DNS 缓存已刷新，设置即刻生效"));
            }
            catch (Exception ex)
            {
                log.Report(new Step(LogLevel.Warn, $"DNS 刷新未成功：{ex.Message}（可手动执行 ipconfig /flushdns）"));
            }

            log.Report(new Step(LogLevel.Ok, $"完成。数据时间：{remoteInfo.UpdateTime}"));
            WriteLog("update ok");
            return new OpResult { Ok = true };
        }
        catch (Exception ex)
        {
            var msg = FriendlyError(ex);
            WriteLog("update failed: " + msg);
            return new OpResult { Ok = false, Error = msg };
        }
    }

    public static Task<OpResult> FlushAsync(IProgress<Step> log)
    {
        try
        {
            log.Report(new Step(LogLevel.Info, "正在执行 ipconfig /flushdns …"));
            var output = FlushDns();
            var tail = string.Join(" ", output.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).TakeLast(2));
            log.Report(new Step(LogLevel.Ok, string.IsNullOrEmpty(tail) ? "DNS 缓存已刷新" : $"已刷新：{tail}"));
            WriteLog("flushdns ok");
            return Task.FromResult(new OpResult { Ok = true });
        }
        catch (Exception ex)
        {
            var msg = FriendlyError(ex);
            WriteLog("flush failed: " + msg);
            return Task.FromResult(new OpResult { Ok = false, Error = msg });
        }
    }

    public static Task<OpResult> ResetAsync(IProgress<Step> log)
    {
        try
        {
            var (curText, enc, bom) = ReadHosts();
            var before = Analyze(curText);

            var bak = MakeBackup("reset");
            log.Report(new Step(LogLevel.Muted, $"已备份当前 hosts → backup\\{bak}"));

            // 以「是否存在 Start 标记」为准：即使段落损坏（只有 Start 没有 End）也能清除
            if (!before.HasBlock && !curText.Contains(StartMark, StringComparison.Ordinal))
            {
                log.Report(new Step(LogLevel.Warn, "当前 hosts 中不存在 GitHub520 段落，无需清除"));
                return Task.FromResult(new OpResult { Ok = true });
            }
            log.Report(new Step(LogLevel.Info, $"正在移除 GitHub520 段落（{before.Count} 条）…"));
            var cleaned = StripBlock(curText);
            WriteHosts(cleaned.Length > 0 ? cleaned + "\r\n" : "", enc, bom);
            log.Report(new Step(LogLevel.Ok, $"已移除 {before.Count} 条 GitHub520 记录，其他自定义内容保持不变"));

            log.Report(new Step(LogLevel.Info, "正在刷新 DNS 缓存…"));
            try
            {
                FlushDns();
                log.Report(new Step(LogLevel.Ok, "DNS 缓存已刷新"));
            }
            catch (Exception ex)
            {
                log.Report(new Step(LogLevel.Warn, $"DNS 刷新未成功：{ex.Message}（可手动执行 ipconfig /flushdns）"));
            }

            WriteLog("reset ok");
            return Task.FromResult(new OpResult { Ok = true });
        }
        catch (Exception ex)
        {
            var msg = FriendlyError(ex);
            WriteLog("reset failed: " + msg);
            return Task.FromResult(new OpResult { Ok = false, Error = msg });
        }
    }

    /// <summary>把底层异常翻译成用户能看懂的中文提示。</summary>
    private static string FriendlyError(Exception ex) => ex switch
    {
        FileNotFoundException => "找不到 hosts 文件，系统环境可能异常",
        DirectoryNotFoundException => "hosts 所在目录不存在，系统环境可能异常",
        UnauthorizedAccessException => "没有写入 hosts 的权限，请以管理员身份运行本工具",
        _ => ex.Message,
    };

    public static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(
                Path.Combine(LogDir, "app.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n",
                Encoding.UTF8);
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
