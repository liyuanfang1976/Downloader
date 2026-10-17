using System.Text.Json;

namespace Downloader;

// tasks.json 是明文的，因此这里只持久化 Referer——Cookie/Authorization 等凭据类头
// 一律只活在内存里（DownloadTask.RequestHeaders），重启后需重新经浏览器下载获得
public record TaskRecord(string Url, string SaveDir, string FileName, long TotalBytes, string Status, string? Sha256 = null, string? Referer = null);

public static class TaskStore
{
    private static readonly object WriteLock = new();

    public static void Save(List<TaskRecord> records)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.Root);
            // 加锁防止并发写互相覆盖，先写临时文件再原子替换，读取方永远不会看到半截内容
            lock (WriteLock)
            {
                string tempPath = AppDataPaths.TaskFile + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(records));
                File.Move(tempPath, AppDataPaths.TaskFile, overwrite: true);
            }
            AppLog.Write($"tasks saved: {records.Count} record(s)");
        }
        catch (Exception ex)
        {
            // 持久化失败不影响运行，但必须留痕：重启后任务列表对不上时靠这条定位
            AppLog.Write($"tasks save FAILED: {ex.Message}");
        }
    }

    public static List<TaskRecord> Load(out string? failure)
    {
        failure = null;
        try
        {
            string path = AppDataPaths.TaskFile;
            if (!File.Exists(path))
            {
                return [];
            }
            return JsonSerializer.Deserialize<List<TaskRecord>>(File.ReadAllText(path))
                ?? throw new JsonException("内容为空");
        }
        catch (Exception ex)
        {
            failure = $"任务列表读取失败，本次将从空列表开始：{ex.Message}";
            return [];
        }
    }
}
