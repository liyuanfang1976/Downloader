using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace Downloader;

public record DownloadProgress(long ReceivedBytes, long TotalBytes, double SpeedBytesPerSec);

// 滑动窗口测速：取最近 1 秒的字节增量算平均速度，
// 消除 TCP 突发导致的瞬时 0 速（否则 UI 会在真实速度和 "-" 之间跳动）
sealed class SpeedMeter
{
    private const double WindowSeconds = 1.0;
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();

    public double Measure(long receivedBytes)
    {
        long now = Stopwatch.GetTimestamp();
        _samples.Enqueue((now, receivedBytes));
        while (_samples.Count > 1
            && (now - _samples.Peek().Timestamp) / (double)Stopwatch.Frequency > WindowSeconds)
        {
            _samples.Dequeue();
        }
        var oldest = _samples.Peek();
        double elapsed = (now - oldest.Timestamp) / (double)Stopwatch.Frequency;
        return elapsed > 0 ? (receivedBytes - oldest.Bytes) / elapsed : 0;
    }
}

public class RemoteFileInfo
{
    public long TotalBytes { get; init; } = -1;
    public bool AcceptRanges { get; init; }
    public string? FileName { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
}

public record SegmentState(long Start, long End, long Downloaded);

// ETag/LastModified 用于恢复时确认远端文件未变，防止把更新过的文件续传出损坏数据
public record DownloadState(string Url, string FileName, long TotalBytes, SegmentState[] Segments, string? ETag = null, string? LastModified = null);

// 全局字节令牌桶：所有任务的所有连接共享一份预算，100ms 补充一次。
// 桶不自动补充而由自己的定时器驱动，这样运行时改限速只需换实例，
// 不存在 Dispose 与在途排队请求的竞争
sealed class BandwidthLimiter
{
    private readonly object _sync = new();
    private TokenBucketRateLimiter? _limiter;
    private System.Threading.Timer? _timer;
    private long _limit;

    public long Limit
    {
        get { lock (_sync) return _limit; }
        set
        {
            lock (_sync)
            {
                _limit = value;
                _limiter = value > 0 ? Create(value) : null;
                if (_limiter != null && _timer == null)
                {
                    _timer = new System.Threading.Timer(_ => Replenish(), null, 20, 20);
                }
                else if (_limiter == null)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        }
    }

    private static TokenBucketRateLimiter Create(long bytesPerSec)
    {
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            // 桶容量 0.2 秒的量：允许小幅突发又不产生肉眼可见的启动突刺
            TokenLimit = (int)Math.Max(bytesPerSec / 5, 1),
            TokensPerPeriod = (int)Math.Max(bytesPerSec / 50, 1),
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(20),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false,
        });
    }

    private void Replenish()
    {
        lock (_sync)
        {
            _limiter?.TryReplenish();
        }
    }

    // 限速时缩小读取块到 ~20ms 的量：读取是速度测量的最小粒度，
    // 块太大（80KB）在低限速下会让测速出现 ±8% 的锯齿
    public int SuggestChunkSize(int defaultSize)
    {
        lock (_sync)
        {
            if (_limit <= 0)
            {
                return defaultSize;
            }
            return (int)Math.Clamp(_limit / 50, 16 * 1024, defaultSize);
        }
    }

    public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        while (bytes > 0)
        {
            TokenBucketRateLimiter? limiter;
            int cap;
            lock (_sync)
            {
                limiter = _limiter;
                // 单次申请不能超过桶容量，否则限速值极低时会抛异常
                cap = (int)Math.Min(Math.Max(_limit / 5, 1), int.MaxValue);
            }
            if (limiter == null)
            {
                return;
            }

            int take = Math.Min(bytes, cap);
            try
            {
                using var lease = await limiter.AcquireAsync(take, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 极小概率：申请排队期间限速被运行时切换，本次按不限速处理
                return;
            }
            bytes -= take;
        }
    }
}

public class DownloadService
{
    private static void Trace(string message) => AppLog.Write(message);

    // 每个文件使用的并发分片数，可由设置页调整
    public int SegmentsPerFile { get; set; } = 8;
    private int SegmentCount => SegmentsPerFile < 1 ? 1 : SegmentsPerFile;

    // 续传时是否跳过 ETag/Last-Modified 校验。地址一致性、总长度、半成品可用性、
    // 分片布局这几项防的是"把 A 文件的断点续到 B 文件上"，属数据错位，任何设置下都校验
    public bool SkipResumeIdentityCheck { get; set; }
    private const long MinSizeForSegments = 4 * 1024 * 1024;
    private const long FlushThreshold = 1024 * 1024;
    private const int ReportIntervalMs = 100;
    private const int SaveStateEveryTicks = 20;

    private readonly HttpClient _http = CreateHttp();

    // 探测只读响应头，必须自带时长上限：半开连接（睡眠唤醒、代理僵死、服务器收下不答）
    // 会让请求永远停在 read 上，下载用的无限超时若沿用过来，弹窗就会无限期"解析中"
    private readonly HttpClient _probeHttp = CreateProbeHttp();

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    // 引擎自己独占控制的头：外部塞进来的一律忽略。
    // Range 被覆盖会让分片写到错误偏移，Host/Connection 被覆盖会直接破坏连接复用
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Range", "Host", "Connection", "Content-Length", "Transfer-Encoding",
    };

    // 不少 CDN（如 B 站 hdslb）有防盗链：无 UA 或无 Referer 时 HEAD/GET 一律回 403，
    // 只有 Thunder、IDM 这类带浏览器头的客户端能识别，这里对齐它们的行为
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    private static HttpClient CreateProbeHttp()
    {
        var client = new HttpClient { Timeout = ProbeTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    // 任务自带的头优先；没给 Referer 时补一个同源根路径的，
    // 足以通过绝大多数防盗链规则，且不会把来源暴露给第三方域。
    // Referer 只能走一条路：实测 HttpRequestHeaders.Referrer 属性设过之后再
    // TryAddWithoutValidation("Referer", …) 会拼成 "Referer: a, b" 双值，防盗链判非法
    private static void ApplyTaskHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        bool hasReferer = false;
        if (headers is { Count: > 0 })
        {
            foreach (var (name, value) in headers)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                if (ReservedHeaders.Contains(name))
                {
                    Trace($"header ignored (reserved by engine): {name}");
                    continue;
                }
                if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(value, UriKind.Absolute, out var referer))
                    {
                        request.Headers.Referrer = referer;
                        hasReferer = true;
                    }
                    else
                    {
                        Trace($"referer rejected (not an absolute uri): {value.Length} chars");
                    }
                    continue;
                }
                // 先删同名再加：字典默认区分大小写，{"Cookie":"a","cookie":"b"} 这种
                // 大小写变体的重复键会被 TryAdd 拼成 "Cookie: a, b"
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!hasReferer && request.RequestUri is { } uri)
        {
            request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        }
    }
    private readonly BandwidthLimiter _bandwidth = new();

    // 全局限速（字节/秒），0 = 不限速，可运行时修改
    public long SpeedLimitBytesPerSec
    {
        get => _bandwidth.Limit;
        set => _bandwidth.Limit = value;
    }

    public async Task<RemoteFileInfo> ProbeAsync(string url, CancellationToken ct, Dictionary<string, string>? headers = null)
    {
        try
        {
            try
            {
                return await ProbeWithHead(url, headers, ct);
            }
            catch (HttpRequestException)
            {
                // 只有 HEAD 明确被拒（403/405 等）才换 Range GET 再试一次；
                // 超时是连接不答，换方法也一样，不必再等一个 30 秒
                return await ProbeWithRangeGet(url, headers, ct);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient 的超时同样抛 TaskCanceledException。不换成独立类型的话，
            // 调用方会把"服务器 30 秒没答"误判成"用户点了暂停/取消"
            throw new TimeoutException($"连接超时（{ProbeTimeout.TotalSeconds:0} 秒内服务器无响应）", ex);
        }
    }

    private async Task<RemoteFileInfo> ProbeWithHead(string url, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        ApplyTaskHeaders(request, headers);
        using var response = await _probeHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return new RemoteFileInfo
        {
            TotalBytes = response.Content.Headers.ContentLength ?? -1,
            AcceptRanges = response.Headers.AcceptRanges.Contains("bytes"),
            FileName = Utils.GetFileNameFromContentDisposition(
                response.Content.Headers.ContentDisposition?.ToString()),
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified?.ToString("R"),
        };
    }

    private async Task<RemoteFileInfo> ProbeWithRangeGet(string url, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        ApplyTaskHeaders(request, headers);
        using var response = await _probeHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = -1;
        bool acceptRanges = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        string? contentRange = response.Content.Headers.ContentRange?.ToString();
        if (!string.IsNullOrEmpty(contentRange))
        {
            int slash = contentRange.LastIndexOf('/');
            if (slash >= 0 && long.TryParse(contentRange[(slash + 1)..], out long parsed))
            {
                total = parsed;
            }
        }
        if (total < 0)
        {
            total = response.Content.Headers.ContentLength ?? -1;
        }

        return new RemoteFileInfo
        {
            TotalBytes = total,
            AcceptRanges = acceptRanges,
            FileName = Utils.GetFileNameFromContentDisposition(
                response.Content.Headers.ContentDisposition?.ToString()),
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified?.ToString("R"),
        };
    }

    // 任一侧缺失时跳过该校验（部分服务器不返回这些头）；两侧都有且不同才判定文件已变
    private bool RemoteFileMatches(DownloadState saved, DownloadTask task)
    {
        if (SkipResumeIdentityCheck)
        {
            return true;
        }
        return (saved.ETag == null || task.ETag == null
                || string.Equals(saved.ETag, task.ETag, StringComparison.Ordinal))
            && (saved.LastModified == null || task.LastModified == null
                || string.Equals(saved.LastModified, task.LastModified, StringComparison.OrdinalIgnoreCase));
    }

    private static string FileChangedReason(DownloadState saved, DownloadTask task)
    {
        bool etagChanged = saved.ETag != null && task.ETag != null
            && !string.Equals(saved.ETag, task.ETag, StringComparison.Ordinal);
        bool timeChanged = saved.LastModified != null && task.LastModified != null
            && !string.Equals(saved.LastModified, task.LastModified, StringComparison.OrdinalIgnoreCase);
        return etagChanged && timeChanged ? "服务器上的文件已更新（ETag 与修改时间均变化）"
            : etagChanged ? "服务器上的文件已更新（ETag 变化）"
            : "服务器上的文件已更新（修改时间变化）";
    }

    // 可读格式在两者接近时会撞车（5242880 与 5242881 都显示成 5 MB），
    // 那种情况下必须退回精确字节数，否则提示等于没说
    private static string SizeChangedReason(long savedTotal, long total)
    {
        string before = Utils.FormatBytes(savedTotal), now = Utils.FormatBytes(total);
        return before == now
            ? $"文件大小已变更（原 {savedTotal:N0} 字节，现 {total:N0} 字节）"
            : $"文件大小已变更（原 {before}，现 {now}）";
    }

    // 顺序与原布尔链一致，只是把每一关的失败原因带出来。
    // saved 为 null 说明没有断点文件，属正常首次下载，无需向用户解释
    private (bool Resume, string? Reason) EvaluateSegmentedResume(
        DownloadTask task, DownloadState? saved, (long Start, long End)[] segments, long total, bool dataFileUsable)
    {
        if (saved == null)
        {
            return (false, null);
        }
        if (!RemoteFileMatches(saved, task))
        {
            return (false, FileChangedReason(saved, task));
        }
        if (!SameRemoteUrl(saved.Url, task.Url))
        {
            return (false, "下载地址已变更");
        }
        if (saved.TotalBytes != total)
        {
            return (false, SizeChangedReason(saved.TotalBytes, total));
        }
        if (!dataFileUsable)
        {
            return (false, "本地文件缺失或大小不足");
        }
        // 存档是单段布局说明上次走了单线程（文件小，或服务器不收 Range 回退过），
        // 那段进度归单线程路径解释，这里不重复出声
        if (saved.Segments.Length == 1)
        {
            return (false, null);
        }
        if (saved.Segments.Length != SegmentCount)
        {
            return (false, $"分片数已变更（原 {saved.Segments.Length}，现 {SegmentCount}）");
        }
        bool layoutMatches = saved.Segments.Zip(segments).All(p =>
            p.First.Start == p.Second.Start
            && p.First.End == p.Second.End
            && p.First.Downloaded >= 0
            && p.First.Downloaded <= p.Second.End - p.Second.Start + 1);
        return layoutMatches ? (true, null) : (false, "分片布局已变更");
    }

    // 签名直链每次点开 query 都在变（?sign=…&ts=…），但那根字节条子还是同一根。
    // 逐字比对整条 URL 等于让"浏览器接管"场景永远续传不上，
    // 所以只比 scheme+host+port+path；真换了文件由 TotalBytes 和 ETag/LastModified 那两关拦。
    // 保守按大小写敏感比：path 在多数 CDN 上区分大小写，忽略大小写会把两个不同文件当成同一个
    private static bool SameRemoteUrl(string savedUrl, string currentUrl)
        => string.Equals(UrlIdentity(savedUrl), UrlIdentity(currentUrl), StringComparison.Ordinal);

    internal static string UrlIdentity(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : url;

    // 半成品被人在资源管理器里删掉时，.dtask 会留下来（两者不是同一个删除入口）。
    // 不校验的话会判定可续传，然后 FileStream(FileMode.Open) 抛 FileNotFound，
    // 退避重试 15 秒后失败；而且状态文件修不好，每次点开始都重复同一场失败。
    // 分段下载一开始就预分配到了 total，所以长度不足同样算不可用
    private static bool SavedDataFileUsable(DownloadTask task, long total)
    {
        try
        {
            var file = new FileInfo(task.FilePath);
            return file.Exists && file.Length >= total;
        }
        catch
        {
            // 路径不可达（网络盘断开、被占用查询失败）一律按不可用处理，退回重下
            return false;
        }
    }

    public async Task DownloadAsync(DownloadTask task, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        // 只记名字不记值：trace.log 是明文落盘的，Cookie 值写进去等于把凭据抄进日志
        if (task.RequestHeaders is { Count: > 0 } hdrs)
        {
            Trace($"task {task.FileName} carries {hdrs.Count} custom header(s): [{string.Join(", ", hdrs.Keys)}]");
        }

        // 每次尝试都重新判定，上一次留下的解释不许带到这一次
        task.ResumeNote = null;

        if (task.AcceptRanges && task.TotalBytes > MinSizeForSegments)
        {
            await DownloadSegmentedAsync(task, progress, ct).ConfigureAwait(false);
        }
        else
        {
            await DownloadSingleAsync(task, progress, ct).ConfigureAwait(false);
        }

        // 两条路径的句柄都已随各自方法退出而关闭，此时写时间戳不会撞 sharing violation；
        // 只有成功才走到这里（异常直接穿透），语义上正是"内容收全"的时刻
        ApplyServerTimestamp(task);
    }

    private async Task DownloadSegmentedAsync(DownloadTask task, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        long total = task.TotalBytes;
        long segmentSize = total / SegmentCount;
        var segments = new (long Start, long End)[SegmentCount];
        for (int i = 0; i < SegmentCount; i++)
        {
            long start = i * segmentSize;
            long end = i == SegmentCount - 1 ? total - 1 : start + segmentSize - 1;
            segments[i] = (start, end);
        }

        Directory.CreateDirectory(task.SaveDir);

        var saved = TryLoadState(task);
        bool dataFileUsable = SavedDataFileUsable(task, total);
        var (resume, resumeReason) = EvaluateSegmentedResume(task, saved, segments, total, dataFileUsable);
        task.ResumeNote = resumeReason;

        Trace($"resume decision: stateFound={saved != null} → resume={resume}" +
              (resumeReason is null ? "" : $"，原因: {resumeReason}"));
        if (saved != null && !resume)
        {
            for (int i = 0; i < saved.Segments.Length && i < SegmentCount; i++)
            {
                long segLen = segments[i].End - segments[i].Start + 1;
                Trace($"  seg{i}: saved(start={saved.Segments[i].Start},end={saved.Segments[i].End},dl={saved.Segments[i].Downloaded}) " +
                      $"expect(start={segments[i].Start},end={segments[i].End},len={segLen})");
            }
        }

        long[] segmentProgress;
        if (resume)
        {
            segmentProgress = saved!.Segments.Select(s => s.Downloaded).ToArray();
        }
        else
        {
            segmentProgress = new long[SegmentCount];
            await using (var prealloc = new FileStream(task.FilePath, FileMode.Create, FileAccess.Write))
            {
                prealloc.SetLength(total);
            }
        }

        // 实时计数器按每次读取更新，供速度显示用；
        // segmentProgress 只在落盘时更新（断点必须保证已写入磁盘），粒度粗不能拿来测速
        long[] segmentLive = resume ? (long[])segmentProgress.Clone() : new long[SegmentCount];

        using var segmentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool finished = false;
        var downloads = new Task[SegmentCount];
        try
        {
            for (int i = 0; i < SegmentCount; i++)
            {
                int index = i;
                long segmentLength = segments[index].End - segments[index].Start + 1;

                // 已完成的分片直接跳过，否则会发出起点>终点的非法 Range 请求
                if (segmentProgress[index] >= segmentLength)
                {
                    downloads[i] = Task.CompletedTask;
                    continue;
                }

                downloads[i] = DownloadSegmentWithRetryAsync(task, segments[index].Start, segments[index].End, segmentProgress, segmentLive, index, segmentCts.Token, resume ? 3 : 1);
            }

            var reporter = Task.Run(() => ReportProgressAsync(progress,
                () => segmentLive.Sum(),
                () => total,
                () => SaveState(task, segments, segmentProgress),
                segmentCts.Token));

            try
            {
                await Task.WhenAll(downloads).ConfigureAwait(false);
            }
            finally
            {
                segmentCts.Cancel();
                try
                {
                    await reporter.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            // 预分配把文件撑到了 total，所以"文件大小对"完全不等于"下完了"：
            // 实测台架 /short 场景 8 段各只收到 5% 仍报成功、长度精确正确、60% 是 0 空洞。
            // 只能自己核对实收字节，且必须在 DeleteState 之前——状态留着重试时才只补缺口
            long receivedTotal = segmentProgress.Sum();
            if (receivedTotal != total)
            {
                Trace($"incomplete: received={receivedTotal} expected={total} per-seg=[{string.Join(", ", segmentProgress)}]");
                throw new IncompleteDownloadException(
                    $"服务器只给了 {receivedTotal}/{total} 字节");
            }

            DeleteState(task);
            finished = true;
        }
        catch (NotSupportedException)
        {
            Trace("falling back to single-thread download");
            // 续传中的分段进度作废了，用户会看到任务从头跑，必须说明原因
            if (resume)
            {
                task.ResumeNote = "服务器不接受分段下载，已改为单线程重新下载";
            }
            // 等所有分片连接完全停止后再退回单线程，避免文件句柄冲突
            try
            {
                await Task.WhenAll(downloads).ConfigureAwait(false);
            }
            catch
            {
            }
            // 服务器忽略了 Range 请求返回整包内容，退回单线程下载
            await DownloadSingleAsync(task, progress, ct).ConfigureAwait(false);
            finished = true;
        }
        finally
        {
            if (!finished)
            {
                // 等所有分片的取消处理跑完再保存：WhenAll 一抛异常就来存，
                // 存到的是还没刷盘记录的旧断点
                try
                {
                    await Task.WhenAll(downloads).ConfigureAwait(false);
                }
                catch
                {
                }
                SaveState(task, segments, segmentProgress);
            }
        }
    }

    // 指数退避重试间隔，总覆盖约 15 秒，容忍杀毒软件扫描大文件的锁定窗口
    private static readonly int[] RetryDelaysMs = [500, 1000, 2000, 4000, 8000];

    private async Task DownloadSegmentWithRetryAsync(DownloadTask task, long segmentStart, long segmentEnd, long[] segmentProgress, long[] segmentLive, int index, CancellationToken ct, int maxAttempts)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // 每次尝试都从该分片已落盘的位置续起，避免重试时重下已完成的字节
                long resumeFrom = segmentStart + segmentProgress[index];
                await DownloadSegmentAsync(task, segmentStart, resumeFrom, segmentEnd, segmentProgress, segmentLive, index, ct).ConfigureAwait(false);
                return;
            }
            catch (NotSupportedException) when (attempt < maxAttempts)
            {
                // 恢复下载时 CDN 偶发返回非 206，稍后重试
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt <= RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[attempt - 1], ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt <= RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[attempt - 1], ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadSegmentAsync(DownloadTask task, long segmentStart, long downloadStart, long segmentEnd, long[] segmentProgress, long[] segmentLive, int index, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(downloadStart, segmentEnd);
        ApplyTaskHeaders(request, task.RequestHeaders);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            Trace($"seg{index}: NON-206 status={response.StatusCode} range={downloadStart}-{segmentEnd}");
            throw new NotSupportedException();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(task.FilePath, FileMode.Open, FileAccess.Write, FileShare.Write, 81920, useAsync: true);
        file.Seek(downloadStart, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long offset = downloadStart;
        long flushed = downloadStart;
        int read;
        try
        {
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, _bandwidth.SuggestChunkSize(buffer.Length)), ct).ConfigureAwait(false)) > 0)
            {
                await _bandwidth.AcquireAsync(read, ct).ConfigureAwait(false);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                offset += read;
                segmentLive[index] = offset - segmentStart;
                if (offset - flushed >= FlushThreshold)
                {
                    // 先落盘再记录进度，保证记录的断点一定已写入磁盘
                    await file.FlushAsync(ct).ConfigureAwait(false);
                    flushed = offset;
                    segmentProgress[index] = offset - segmentStart;
                }
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
            segmentProgress[index] = offset - segmentStart;
        }
        catch (OperationCanceledException)
        {
            // 与单线程路径对称：暂停/退出时先把缓冲区刷盘，按真实写入位置记断点。
            // 只靠循环里 1MB 的落盘门槛记断点的话，低限速下整个下载期间都跨不过一次，
            // 已显示进度的字节全部作废——用户看到的就是"每次暂停后再开始都从头下"
            try
            {
                file.Flush();
                segmentProgress[index] = file.Position - segmentStart;
                segmentLive[index] = segmentProgress[index];
            }
            catch
            {
            }
            throw;
        }
    }

    private async Task ReportProgressAsync(IProgress<DownloadProgress> progress, Func<long> getReceived, Func<long> getTotal, Action saveState, CancellationToken ct)
    {
        var meter = new SpeedMeter();
        int tick = 0;
        try
        {
            while (true)
            {
                await Task.Delay(ReportIntervalMs, ct).ConfigureAwait(false);
                long received = getReceived();
                progress.Report(new DownloadProgress(received, getTotal(), meter.Measure(received)));

                if (++tick % SaveStateEveryTicks == 0)
                {
                    try
                    {
                        saveState();
                    }
                    catch (Exception ex)
                    {
                        // 定期保存失败可容忍，暂停/退出时还会再存一次；但断点丢失必须留痕
                        AppLog.Write($"periodic state save FAILED: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress.Report(new DownloadProgress(getReceived(), getTotal(), 0));
        }
    }

    private async Task DownloadSingleAsync(DownloadTask task, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(task.SaveDir);

        (long resumeFrom, string? resumeReason) = TryLoadSingleResume(task);
        // 从分段路径回退而来时，进度为什么作废已经解释过了，不能被覆盖成空
        task.ResumeNote = resumeReason ?? task.ResumeNote;
        if (resumeFrom <= 0)
        {
            DeleteState(task);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
        ApplyTaskHeaders(request, task.RequestHeaders);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (resumeFrom > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // 服务器忽略了续传 Range 返回整包内容，只能从头开始
            Trace("single: resume range rejected, restarting from 0");
            resumeFrom = 0;
            task.ResumeNote = "服务器不接受断点续传，已从头下载";
        }
        response.EnsureSuccessStatusCode();

        if (resumeFrom > 0)
        {
            long remaining = response.Content.Headers.ContentLength ?? -1;
            if (task.TotalBytes <= 0 && remaining > 0)
            {
                task.TotalBytes = resumeFrom + remaining;
            }
        }
        else
        {
            task.TotalBytes = response.Content.Headers.ContentLength ?? -1;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(
            task.FilePath,
            resumeFrom > 0 ? FileMode.Open : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);
        if (resumeFrom > 0)
        {
            file.Seek(resumeFrom, SeekOrigin.Begin);
        }

        var buffer = new byte[81920];
        long received = resumeFrom;
        long flushed = resumeFrom;
        var meter = new SpeedMeter();
        long lastReportTimestamp = Stopwatch.GetTimestamp();
        int read;

        try
        {
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, _bandwidth.SuggestChunkSize(buffer.Length)), ct).ConfigureAwait(false)) > 0)
            {
                await _bandwidth.AcquireAsync(read, ct).ConfigureAwait(false);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (received - flushed >= FlushThreshold)
                {
                    // 先落盘再记录进度，保证记录的断点一定已写入磁盘
                    await file.FlushAsync(ct).ConfigureAwait(false);
                    flushed = received;
                    SaveState(task, [(0, task.TotalBytes > 0 ? task.TotalBytes - 1 : -1)], [flushed]);
                }

                long now = Stopwatch.GetTimestamp();
                if ((now - lastReportTimestamp) / (double)Stopwatch.Frequency >= ReportIntervalMs / 1000.0)
                {
                    progress.Report(new DownloadProgress(received, task.TotalBytes, meter.Measure(received)));
                    lastReportTimestamp = now;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消时把缓冲数据尽力落盘并记录断点，下次恢复少重传
            try
            {
                file.Flush();
                SaveState(task, [(0, task.TotalBytes > 0 ? task.TotalBytes - 1 : -1)], [file.Position]);
            }
            catch
            {
            }
            throw;
        }

        await file.FlushAsync(ct).ConfigureAwait(false);
        // 与分段路径同理：核对实收字节。TotalBytes 未知（服务器没给 Content-Length）时无从核对，只能放过
        if (task.TotalBytes > 0 && received != task.TotalBytes)
        {
            Trace($"single incomplete: received={received} expected={task.TotalBytes}");
            SaveState(task, [(0, task.TotalBytes - 1)], [received]);
            throw new IncompleteDownloadException(
                $"服务器只给了 {received}/{task.TotalBytes} 字节");
        }
        DeleteState(task);
        progress.Report(new DownloadProgress(received, task.TotalBytes, 0));
    }

    // 单线程下载与分段下载共用 .dtask：单分片布局 (0, total-1, 已下载字节)，
    // 因此 SegmentsPerFile=1 的分段状态也能被这里识别，反之亦然
    private (long ResumeFrom, string? Reason) TryLoadSingleResume(DownloadTask task)
    {
        try
        {
            if (!File.Exists(StatePath(task)))
            {
                return (0, null);
            }
            var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(StatePath(task)));
            // 布局不是单段或没有已下载字节：属于下载方式切换（分段↔单线程）或本无可续内容，
            // 解释权归另一条路径，这里不重复出声
            if (state is not { Segments.Length: 1 }
                || state.Segments[0].Start != 0
                || state.Segments[0].Downloaded <= 0)
            {
                return (0, null);
            }
            if (!SameRemoteUrl(state.Url, task.Url))
            {
                return (0, "下载地址已变更");
            }
            if (!RemoteFileMatches(state, task))
            {
                return (0, FileChangedReason(state, task));
            }

            long downloaded = state.Segments[0].Downloaded;
            if (task.TotalBytes > 0)
            {
                if (state.Segments[0].End >= 0 && state.Segments[0].End != task.TotalBytes - 1)
                {
                    return (0, SizeChangedReason(state.Segments[0].End + 1, task.TotalBytes));
                }
                if (downloaded >= task.TotalBytes)
                {
                    return (0, null);
                }
            }

            var file = new FileInfo(task.FilePath);
            if (!file.Exists || file.Length < downloaded)
            {
                return (0, "本地文件缺失或大小不足");
            }
            return (downloaded, null);
        }
        catch
        {
            return (0, null);
        }
    }

    private static string StatePath(DownloadTask task) => task.FilePath + ".dtask";

    private void SaveState(DownloadTask task, (long Start, long End)[] segments, long[] segmentProgress)
    {
        Trace($"save state: [{string.Join(", ", segmentProgress)}] (sum={segmentProgress.Sum()})");
        var state = new DownloadState(
            task.Url,
            task.FileName,
            task.TotalBytes,
            segments.Select((s, i) => new SegmentState(s.Start, s.End, segmentProgress[i])).ToArray(),
            task.ETag,
            task.LastModified);
        string json = JsonSerializer.Serialize(state);
        // 先写临时文件再原子替换：读取方（重启时的 TryLoadState、界面上的进度恢复）
        // 永远不会看到半截内容，进程被杀也不会把状态写坏而白白丢掉进度
        string statePath = StatePath(task);
        File.WriteAllText(statePath + ".tmp", json);
        File.Move(statePath + ".tmp", statePath, overwrite: true);
    }

    private static DownloadState? TryLoadState(DownloadTask task)
    {
        try
        {
            if (!File.Exists(StatePath(task)))
            {
                Trace("load state: file missing");
                return null;
            }
            var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(StatePath(task)));
            Trace(state == null ? "load state: deserialized null" : "load state: ok");
            return state;
        }
        catch (Exception ex)
        {
            Trace($"load state failed: {ex.Message}");
            return null;
        }
    }

    private static void DeleteState(DownloadTask task)
    {
        try
        {
            string statePath = StatePath(task);
            File.Delete(statePath);
            // 原子替换的临时文件：进程正好被杀在 WriteAllText 与 Move 之间时会剩一个
            File.Delete(statePath + ".tmp");
        }
        catch
        {
        }
    }

    // 把服务端的 Last-Modified 写回文件时间（IDM 同款）：文件带上的是内容的时间戳而不是
    // 下载时间，资源管理器里排序、辨版本才有意义。创建和修改时间都写——修改时间是默认
    // 列里唯一可见的，只写创建时间等于肉眼看不见
    private static void ApplyServerTimestamp(DownloadTask task)
    {
        if (task.LastModified is not { } lastModified)
        {
            AppLog.Write($"timestamp skipped: {task.FileName} (server sent no Last-Modified)");
            return;
        }

        try
        {
            if (!DateTimeOffset.TryParse(lastModified, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var serverTime))
            {
                AppLog.Write($"timestamp skipped: {task.FileName} Last-Modified unparsable: {lastModified}");
                return;
            }

            var utc = serverTime.UtcDateTime;
            File.SetCreationTimeUtc(task.FilePath, utc);
            File.SetLastWriteTimeUtc(task.FilePath, utc);
            AppLog.Write($"timestamp: {task.FileName} → {utc:yyyy-MM-dd HH:mm:ss}Z (server Last-Modified)");
        }
        catch (Exception ex)
        {
            // 文件被占用（杀毒扫描、用户刚好打开）等：时间戳写不了不影响下载已完成的事实
            AppLog.Write($"timestamp write-back failed: {task.FileName}: {ex.Message}");
        }
    }
}
