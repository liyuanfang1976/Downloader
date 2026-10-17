using System.Text.Json;

namespace Downloader;

public sealed record HandoffTask(
    string Url,
    string? FileName = null,
    string? SaveDir = null,
    Dictionary<string, string>? Headers = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static HandoffTask? TryParse(string json, out string? error)
    {
        try
        {
            error = null;
            return JsonSerializer.Deserialize<HandoffTask>(json, Options);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

public static class Handoff
{
    public const string SingleInstanceMutexName = @"Local\Downloader_SingleInstance";
    public const string InboxFolderName = "inbox";
    public const string AppExeName = "Downloader.exe";
    public const string HostExeName = "Downloader.NativeHost.exe";
    // 由 extension\DownloaderHandoff.pem 决定，一锁三用：crx 签名、allowed_origins、注册表键名
    public const string ExtensionId = "mhdhfggpookgphfnepgemppfhjdpdhhc";
}
