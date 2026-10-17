using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Downloader;

// --header "Name: value" 可重复，用于验引擎是否把浏览器带来的头透传出去；
// 先把它摘掉，剩下的仍按原来的位置参数解析，老命令行写法不受影响。
//   --skip-check     打开"续传跳过 ETag/Last-Modified 校验"开关
//   --remote-changed 第二次探测后伪造一个变过的 ETag，模拟服务端换了文件
//   --resume-url U   第二次探测/下载改用 U，用来制造"重启后服务器行为变了"的场景
//   --cycles N       第一次下载改成连续 N 次「开始→暂停」循环，每次暂停后打印断点，
//                    用来验"暂停时已下载的进度有没有被记住"（配合限速参数使用）
var customHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var positional = new List<string>();
bool skipCheck = false;
bool remoteChanged = false;
string? resumeUrl = null;
int cycles = 1;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--header" or "-h":
            if (i + 1 >= args.Length)
            {
                Console.WriteLine("--header 后面缺少 \"Name: value\"");
                return 1;
            }
            string pair = args[++i];
            int colon = pair.IndexOf(':');
            if (colon <= 0)
            {
                Console.WriteLine($"--header 需要 \"Name: value\" 形式，收到: {pair}");
                return 1;
            }
            customHeaders[pair[..colon].Trim()] = pair[(colon + 1)..].Trim();
            continue;
        case "--skip-check":
            skipCheck = true;
            continue;
        case "--remote-changed":
            remoteChanged = true;
            continue;
        case "--resume-url":
            if (i + 1 >= args.Length)
            {
                Console.WriteLine("--resume-url 后面缺少 URL");
                return 1;
            }
            resumeUrl = args[++i];
            continue;
        case "--cycles":
            if (i + 1 >= args.Length)
            {
                Console.WriteLine("--cycles 后面缺少次数");
                return 1;
            }
            cycles = int.Parse(args[++i]);
            continue;
    }
    positional.Add(args[i]);
}

if (positional.Count == 0)
{
    Console.WriteLine("usage: EngineTest <url> [pauseAfterSeconds] [saveDir] [limitMBs] [--cycles N] [--header \"Name: value\"]...");
    return 1;
}

string url = positional[0];
int pauseAfterSec = positional.Count > 1 ? int.Parse(positional[1]) : 3;
string saveDir = positional.Count > 2 ? positional[2] : Path.Combine(Path.GetTempPath(), "dltest");
long? limitBytesPerSec = positional.Count > 3 && long.TryParse(positional[3], out long mb) ? mb * 1024 * 1024 : null;
Directory.CreateDirectory(saveDir);

AppLog.Sink = m => Console.WriteLine($"  [trace] {m}");

string fileName = Utils.GetFileNameFromUrl(url);
fileName = Utils.GetUniqueFileName(saveDir, fileName);
var task = new DownloadTask
{
    Url = url,
    SaveDir = saveDir,
    FileName = fileName,
    RequestHeaders = customHeaders.Count > 0 ? new Dictionary<string, string>(customHeaders) : null,
};
if (customHeaders.Count > 0)
{
    // 只列名字：这台架的输出常被贴进日志/聊天，值里可能有凭据
    Console.WriteLine($"custom headers: [{string.Join(", ", customHeaders.Keys)}]");
}

var service = new DownloadService();
if (limitBytesPerSec is > 0)
{
    service.SpeedLimitBytesPerSec = limitBytesPerSec.Value;
    Console.WriteLine($"speed limit: {limitBytesPerSec / 1048576.0:F0} MB/s");
}

Console.WriteLine($"== probe {url}");
var info = await service.ProbeAsync(url, CancellationToken.None, customHeaders.Count > 0 ? customHeaders : null);
task.TotalBytes = info.TotalBytes;
task.AcceptRanges = info.AcceptRanges;
task.ETag = info.ETag;
task.LastModified = info.LastModified;
Console.WriteLine($"probe: total={info.TotalBytes} acceptRanges={info.AcceptRanges} name={info.FileName} etag={info.ETag} lastMod={info.LastModified}");
if (skipCheck)
{
    service.SkipResumeIdentityCheck = true;
    Console.WriteLine("skip resume identity check: ON");
}

long lastPrint = -1;
var progress = new Progress<DownloadProgress>(p =>
{
    task.ReceivedBytes = p.ReceivedBytes;
    int pct = task.TotalBytes > 0 ? (int)(p.ReceivedBytes * 100 / task.TotalBytes) : 0;
    if (pct != lastPrint && pct % 5 == 0)
    {
        lastPrint = pct;
        Console.WriteLine($"  {pct}%  {p.SpeedBytesPerSec / 1024:0} KB/s");
    }
});

string statePath = task.FilePath + ".dtask";
bool breakpointLost = false;
for (int cycle = 1; cycle <= cycles; cycle++)
{
    Console.WriteLine($"== run 1.{cycle} (will cancel after {pauseAfterSec}s)");
    using (var cts = new CancellationTokenSource())
    {
        var dl = service.DownloadAsync(task, progress, cts.Token);
        await Task.Delay(pauseAfterSec * 1000);
        cts.Cancel();
        try { await dl; }
        catch (OperationCanceledException) { Console.WriteLine($"cycle {cycle} cancelled (simulated pause)"); }
        // 收口断言这类失败要报出来继续往下跑：接着还要验"失败后状态是否留得住、能否续传"
        catch (Exception ex) { Console.WriteLine($"cycle {cycle} failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    Console.WriteLine($"paused at: {task.ReceivedBytes} bytes ({(task.TotalBytes > 0 ? task.ReceivedBytes * 100 / task.TotalBytes : 0)}%)");
    Console.WriteLine($"resume note after cycle {cycle}: {task.ResumeNote ?? "(none)"}");
    Console.WriteLine(File.Exists(statePath)
        ? $".dtask content: {File.ReadAllText(statePath)}"
        : ".dtask MISSING!");

    // 回归断言（2026-10-15 用户实测 bug）：限速 + 分段路径下暂停后，断点必须已经前进。
    // 分段路径原来只按 1MB 落盘门槛记断点，1MB/s 摊到每个分片后几秒钟跨不过一次，
    // 于是每次暂停再开始都从头下。不限速时整包秒下完、没有断点可留，故只在限速时断言
    if (limitBytesPerSec is > 0 && pauseAfterSec >= 2 && File.Exists(statePath))
    {
        var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(statePath));
        long saved = state?.Segments.Sum(s => s.Downloaded) ?? 0;
        Console.WriteLine($"breakpoint after pause: {saved} bytes → {(saved > 0 ? "PASS" : "FAIL")}");
        breakpointLost |= saved <= 0;
    }
}

Console.WriteLine("== run 2 (re-probe like the app, then resume)");
if (resumeUrl != null)
{
    Console.WriteLine($"resume url override: {resumeUrl}");
}
var info2 = await service.ProbeAsync(resumeUrl ?? url, CancellationToken.None, customHeaders.Count > 0 ? customHeaders : null);
Console.WriteLine($"probe2: total={info2.TotalBytes} acceptRanges={info2.AcceptRanges} name={info2.FileName ?? "(null)"}");
task.TotalBytes = info2.TotalBytes;
task.AcceptRanges = info2.AcceptRanges;
task.ETag = info2.ETag;
task.LastModified = info2.LastModified;
if (remoteChanged)
{
    // 路径与大小都不变，只有 ETag 变：正是"服务端把同一位置的文件换掉了"
    task.ETag = "\"simulated-remote-change\"";
    Console.WriteLine($"simulated remote change: etag {info2.ETag} → {task.ETag}");
}
if (!string.IsNullOrEmpty(info2.FileName) && info2.FileName != task.FileName)
{
    task.FileName = Utils.GetUniqueFileName(saveDir, info2.FileName);
    Console.WriteLine($"probe2 RENAMED file to {task.FileName}");
}
var sw = Stopwatch.StartNew();
try
{
    if (resumeUrl != null)
    {
        // Url 是 init-only，只能整体换个对象；文件名/目录必须原样带走，否则接不上旧断点
        task = new DownloadTask
        {
            Url = resumeUrl,
            SaveDir = task.SaveDir,
            FileName = task.FileName,
            TotalBytes = task.TotalBytes,
            AcceptRanges = task.AcceptRanges,
            ETag = task.ETag,
            LastModified = task.LastModified,
            RequestHeaders = task.RequestHeaders,
        };
    }
    await service.DownloadAsync(task, progress, CancellationToken.None);
}
catch (Exception ex)
{
    Console.WriteLine($"run 2 failed: {ex.GetType().Name}: {ex.Message}");
}
sw.Stop();
Console.WriteLine($"run 2 finished in {sw.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine($"resume note after run 2: {task.ResumeNote ?? "(none)"}");

long actual = new FileInfo(task.FilePath).Length;
bool ok = info.TotalBytes < 0 || actual == info.TotalBytes;
Console.WriteLine($"file size={actual} expected={info.TotalBytes} → {(ok ? "PASS" : "FAIL")}");

// 时间戳写回：探测拿到了 Last-Modified，下完的文件修改时间就应该是它。
// FAT32 等文件系统对写时间只有 2 秒粒度，容差 2 秒
if (info.LastModified is { } lastMod
    && DateTimeOffset.TryParse(lastMod, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var serverTime))
{
    var written = new FileInfo(task.FilePath).LastWriteTimeUtc;
    bool timeOk = Math.Abs((written - serverTime.UtcDateTime).TotalSeconds) <= 2;
    Console.WriteLine($"file lastWrite={written:yyyy-MM-dd HH:mm:ss}Z expected={serverTime.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z → {(timeOk ? "PASS" : "FAIL")}");
    ok &= timeOk;
}
return ok && !breakpointLost ? 0 : 2;
