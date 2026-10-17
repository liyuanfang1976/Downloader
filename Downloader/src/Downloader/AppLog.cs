namespace Downloader;

// 全程序统一的诊断日志出口：主程序在设置里打开调试日志后接到 trace.log，
// 测试台架接到控制台；没接时全部丢弃。时间戳统一在这里加，避免各写各的格式。
// 注意：调用方可能来自引擎后台线程，落盘的那一端要自己保证线程安全
public static class AppLog
{
    public static Action<string>? Sink { get; set; }

    public static void Write(string message)
        => Sink?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    // 崩溃不走 Sink（那时窗口可能还没起来、调试日志也可能没开），直接落 error-*.log，
    // 否则进程级异常只在 Windows 事件日志里留痕，自己的日志查不到
    public static void WriteCrash(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.LogDir);
            File.AppendAllText(
                Path.Combine(AppDataPaths.LogDir, $"error-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:HH:mm:ss}] {context}\n{ex}\n\n");
        }
        catch
        {
        }
    }
}
