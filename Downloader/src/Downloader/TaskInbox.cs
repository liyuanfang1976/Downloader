namespace Downloader;

public sealed class TaskInbox : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action<HandoffTask> _onReceived;

    public TaskInbox(Action<HandoffTask> onReceived)
    {
        _onReceived = onReceived;
        Directory.CreateDirectory(AppDataPaths.InboxDir);
        _watcher = new FileSystemWatcher(AppDataPaths.InboxDir, "*.json");
        // 同目录内的 File.Move 只产生 Renamed 事件，只挂 Created 会漏掉全部运行时投递
        _watcher.Created += (_, e) => Consume(e.FullPath);
        _watcher.Renamed += (_, e) => Consume(e.FullPath);
        _watcher.Changed += (_, e) => Consume(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    public void Drain()
    {
        foreach (string path in Directory.GetFiles(AppDataPaths.InboxDir, "*.json"))
        {
            Consume(path);
        }
    }

    private void Consume(string path)
    {
        if (!File.Exists(path) || Path.GetExtension(path) != ".json")
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch
        {
            return;
        }

        if (HandoffTask.TryParse(json, out string? parseError) is { Url.Length: > 0 } handoff)
        {
            // 先投递再删：投递失败时单子还在收件箱里，下次启动能补收；
            // 反过来就是文件没了、任务也没建起来，等于永久丢单
            _onReceived(handoff);
            TryDelete(path);
            return;
        }

        TryDelete(path);
        LogRejected(path, parseError);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // 删不掉意味着这个单子之后还会被再消费一次（重复弹窗），必须留痕
            AppLog.Write($"inbox: failed to remove {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void LogRejected(string path, string? reason)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.LogDir);
            File.AppendAllText(
                Path.Combine(AppDataPaths.LogDir, "inbox-error.log"),
                $"[{DateTime.Now:HH:mm:ss}] 拒收 {Path.GetFileName(path)}：{reason}\n");
        }
        catch
        {
        }
    }

    public void Dispose() => _watcher.Dispose();
}
