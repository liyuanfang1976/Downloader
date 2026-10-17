using System.Text.Json;
using System.Text.Json.Serialization;

namespace Downloader;

// 新建任务时撞上列表里已有同一地址任务的处理策略。Ask = 每次弹窗询问
public enum DuplicateLinkPolicy
{
    Ask,
    NewCopy,
    Overwrite,
    ResumeOrComplete,
    Skip,
}

public class AppSettings
{
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int SegmentsPerFile { get; set; } = 8;
    public string DefaultSaveDir { get; set; } = "";
    public bool EnableDebugLog { get; set; }
    public long SpeedLimitBytesPerSec { get; set; }
    // 续传时跳过 ETag/Last-Modified 校验：地址、文件大小、分片布局等防错位的校验不受影响
    public bool SkipResumeIdentityCheck { get; set; }
    // 以字符串形式存，settings.json 里能直接看懂
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DuplicateLinkPolicy DuplicateLinkPolicy { get; set; } = DuplicateLinkPolicy.Ask;
    // 添加链接弹窗显示期间就开始下载（不等点"立即下载"）；关闭弹窗（×/Esc）即取消，
    // 未确认的下载一律删除。默认关：误点链接时读文件名的几秒带宽已经在跑
    public bool StartDownloadWhileDialogOpen { get; set; }

    // 日志与启动摘要共用的一份可读描述；saveDir 空表示每次新建任务时手动选
    public string Describe() => $"concurrent={MaxConcurrentDownloads} segments={SegmentsPerFile} " +
        $"saveDir={(DefaultSaveDir.Length == 0 ? "(每次询问)" : DefaultSaveDir)} " +
        $"limit={(SpeedLimitBytesPerSec > 0 ? SpeedLimitBytesPerSec / 1048576.0 : 0):0.##}MB/s " +
        $"skipCheck={SkipResumeIdentityCheck} debugLog={EnableDebugLog} dupPolicy={DuplicateLinkPolicy}";
}

public static class SettingsStore
{
    public static AppSettings Load(out string? failure)
    {
        failure = null;
        try
        {
            string path = AppDataPaths.SettingsFile;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                ?? throw new JsonException("内容为空");
        }
        catch (Exception ex)
        {
            failure = $"配置文件读取失败，已改用默认设置：{ex.Message}";
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.Root);
            File.WriteAllText(AppDataPaths.SettingsFile, JsonSerializer.Serialize(settings));
            AppLog.Write($"settings saved: {settings.Describe()}");
        }
        catch (Exception ex)
        {
            // 保存失败不影响运行，但必须留痕：否则用户改的设置"莫名其妙"没生效时无从查起
            AppLog.Write($"settings save FAILED: {ex.Message}");
        }
    }
}
