using System.Net.Http;
using System.Text.RegularExpressions;

namespace Downloader;

public static class Utils
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "未知";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "-";
        double mb = bytesPerSec / (1024 * 1024);
        if (mb >= 10) return $"{mb:0.#} MB/s";
        if (mb >= 1) return $"{mb:0.00} MB/s";
        return $"{bytesPerSec / 1024:0} KB/s";
    }

    public static string GetFileNameFromUrl(string url)
    {
        string? name = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(name) || name == "/")
        {
            name = $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    public static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    public static string GetUniqueFileName(string dir, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int i = 1;
        while (File.Exists(Path.Combine(dir, fileName)))
        {
            fileName = $"{baseName}({i}){ext}";
            i++;
        }
        return fileName;
    }

    public static string? GetFileNameFromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var match = Regex.Match(header, @"filename\*\s*=\s*utf-8''(?<name>[^;]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return Uri.UnescapeDataString(match.Groups["name"].Value).Trim('"');
        }

        match = Regex.Match(header, @"filename\s*=\s*""(?<name>[^""]+)""");
        if (match.Success)
        {
            return match.Groups["name"].Value;
        }

        match = Regex.Match(header, @"filename\s*=\s*(?<name>[^;]+)");
        if (match.Success)
        {
            return match.Groups["name"].Value.Trim();
        }

        return null;
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    public static string StatePathFor(DownloadTask task) => task.FilePath + ".dtask";

    // 在头的副本上设置一个值（不动传入的字典）；统一用忽略大小写的字典，
    // 因为扩展传来的头大小写不确定，而 Referer 的读写都按固定拼写走
    public static Dictionary<string, string> WithHeader(Dictionary<string, string>? headers, string name, string value)
    {
        var merged = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        merged[name] = value;
        return merged;
    }

    // 引用页校验：必须是完整的绝对 http/https 地址，与引擎侧 ApplyTaskHeaders 的判定一致。
    // 添加弹窗的补救模态和任务属性窗共用这一份
    public static bool IsValidReferer(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // 面向用户显示的失败原因要短：HttpRequestException 的原始消息是英文长句，
    // 状态列和弹窗行都放不下。完整堆栈进 error-*.log，一行摘要进 trace.log，
    // 都不依赖这段文案，所以这里只管短和准
    public static string DescribeHttpError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"服务器拒绝访问 ({(int)code})",
        HttpRequestException => "无法连接，请检查网络或链接",
        TaskCanceledException => "连接超时",
        _ => ex.Message,
    };

    // 任务当前状态的可读描述（带进度），详情弹窗与日志共用同一份，避免两处说法不一致
    public static string DescribeState(DownloadTask task)
    {
        string pct = task.TotalBytes > 0 ? $" {task.ReceivedBytes * 100 / task.TotalBytes}%" : "";
        return task.Status switch
        {
            DownloadStatus.Completed => "已完成",
            DownloadStatus.Error => $"下载失败：{task.ErrorMessage}",
            DownloadStatus.Downloading => $"正在下载{pct}",
            DownloadStatus.Waiting => $"等待中{pct}",
            DownloadStatus.Retrying => task.ErrorMessage ?? "重试中",
            _ => $"已暂停{pct}",
        };
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 文件被占用等情况下忽略，不影响主流程
        }
    }
}
