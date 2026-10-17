namespace Downloader;

public enum DownloadStatus
{
    Waiting,
    Downloading,
    Retrying,
    Paused,
    Completed,
    Error,
}

public class Sha256MismatchException : Exception
{
    public Sha256MismatchException(string message) : base(message) { }
}

// 服务器把连接"自洽地"提前收尾时，HTTP 层看不出问题（它发的字节数和自己的
// Content-Length 相符），只有引擎核对实收长度才发现，单独成类型以便与网络错误区分
public class IncompleteDownloadException : Exception
{
    public IncompleteDownloadException(string message) : base(message) { }
}

public class DownloadTask
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Url { get; init; }
    public required string SaveDir { get; init; }
    public required string FileName { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long ReceivedBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public bool AcceptRanges { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    // 添加任务时弹窗已探测过，跳过下载前的重复探测；文件名也按用户确认的结果，不再被服务器名覆盖
    public bool Probed { get; set; }
    public bool DeleteRequested { get; set; }
    // 覆盖已有任务时打回重下：先取消本次下载，等它收尾（句柄释放、状态文件落定）后
    // 由 RunTaskAsync 的 finally 重启，避免两个下载同时写同一个文件
    public bool RestartRequested { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ActualSha256 { get; set; }
    // 浏览器接管任务带来的请求头（Referer、Cookie、Authorization 等）。
    // 只活在内存里：Cookie/Authorization 等不进 tasks.json 也不进 .dtask，重启即失效
    public Dictionary<string, string>? RequestHeaders { get; set; }

    // Referer 是唯一被持久化的请求头（TaskRecord.Referer）：防盗链站点重启后续传/重下
    // 仍然带得上。这里是对 RequestHeaders 的投影，不是第二份真值
    public string? Referer
    {
        get => RequestHeaders is { } h && h.TryGetValue("Referer", out var v) ? v : null;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                RequestHeaders?.Remove("Referer");
                return;
            }
            (RequestHeaders ??= new(StringComparer.OrdinalIgnoreCase))["Referer"] = value;
        }
    }
    public DownloadStatus Status { get; set; } = DownloadStatus.Waiting;
    public string? ErrorMessage { get; set; }
    // 引擎判定不可续传时写入的可读原因，让用户知道为什么已下载的进度被丢弃。
    // 与 RequestHeaders 一样只活在内存里：既不进 tasks.json 也不进 .dtask，重启即失效
    public string? ResumeNote { get; set; }

    public string FilePath => Path.Combine(SaveDir, FileName);
}
