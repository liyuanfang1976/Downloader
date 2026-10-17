using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Downloader;

// 浏览器接管在本机的装机与拆机。
// 开关状态不写进 settings.json，每次从注册表与磁盘的真实状态检测：本地缓存与系统会漂移，
// 而删除 data\ 后如果还留着注册表，靠缓存就会永远显示"未启用"、再也收不回来。
public static class BrowserIntegration
{
    public enum State
    {
        Off,            // 本机没有任何接管痕迹
        Registered,     // 三项齐全，且指向当前这份程序
        Incomplete,     // 缺项或版本落后
        OwnedElsewhere, // 注册指向另一个目录里的另一份程序
    }

    public sealed record Snapshot(State Status, string Detail);

    private const string HostName = "com.downloader.host";
    private const string CrxName = "DownloaderHandoff.crx";
    private const string HostJsonName = HostName + ".json";

    private static string HostKeyPath => $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}";
    private static string ExtKeyPath => $@"Software\Google\Chrome\Extensions\{Handoff.ExtensionId}";
    // 新旧两个策略名都写：Chrome 认的是其中一个，写两份免得赌版本
    private static string[] PolicyKeyPaths =>
    [
        @"Software\Policies\Google\Chrome\ExtensionInstallAllowlist",
        @"Software\Policies\Google\Chrome\ExtensionInstallWhitelist",
    ];

    private static string ExeDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private static string CrxPath => Path.Combine(ExeDir, CrxName);
    private static string HostJsonPath => Path.Combine(ExeDir, HostJsonName);
    private static string HostExePath => Path.Combine(ExeDir, Handoff.HostExeName);

    public static Snapshot Probe()
    {
        string? hostJson = ReadDefaultValue(HostKeyPath);
        string? extPath = ReadValue(ExtKeyPath, "path");
        string? extVersion = ReadValue(ExtKeyPath, "version");
        bool policy = PolicyKeyPaths.Any(HasOurAllowlistEntry);

        // 先判"有没有痕迹"：扩展包丢了也不能报成未启用，否则残留的注册就再也收不回来
        if (hostJson is null && extPath is null && !policy)
        {
            return new Snapshot(State.Off, "未启用");
        }

        // 清单路径或扩展路径都记着"当初是哪份程序装的"，据此判断归属
        string? ownerDir = hostJson is { Length: > 0 } ? Path.GetDirectoryName(hostJson)
            : extPath is { Length: > 0 } ? Path.GetDirectoryName(extPath) : null;
        if (ownerDir is { Length: > 0 } && !SameDir(ownerDir, ExeDir))
        {
            return new Snapshot(State.OwnedElsewhere, $"接管由另一份程序注册：{ownerDir}");
        }

        CrxInfo? crx = ReadCrx();
        List<string> missing = [];
        if (crx is null) missing.Add($"{CrxName} 缺失或无法解析");
        else if (crx.Id != Handoff.ExtensionId) missing.Add($"扩展包 ID 不匹配（{crx.Id}）");
        if (hostJson is null) missing.Add("宿主注册");
        else if (!IsOurHostJson(hostJson)) missing.Add("宿主清单内容异常");
        if (extPath is null) missing.Add("扩展登记");
        else if (crx is not null && !SamePath(extPath, CrxPath)) missing.Add("扩展登记指向别处");
        if (!policy) missing.Add("Chrome 策略白名单");
        if (crx is not null && extVersion is not null && extVersion != crx.Version)
        {
            missing.Add($"扩展待更新（{extVersion} → {crx.Version}）");
        }

        return missing.Count == 0
            ? new Snapshot(State.Registered, "已注册；请完全退出并重启 Chrome，首次会问你是否启用")
            : new Snapshot(State.Incomplete, "状态不完整：" + string.Join("、", missing));
    }

    public static Snapshot Enable()
    {
        CrxInfo? crx = ReadCrx();
        if (crx is null)
        {
            AppLog.Write($"integration: enable rejected, crx unusable");
            return new Snapshot(State.Off, $"无法启用：{CrxName} 缺失或无法解析（须与 Downloader.exe 同目录）");
        }
        if (crx.Id != Handoff.ExtensionId)
        {
            AppLog.Write($"integration: enable rejected, crx id mismatch ({crx.Id})");
            return new Snapshot(State.Off, $"无法启用：扩展包与本程序不匹配（包内 ID 为 {crx.Id}）");
        }
        if (!File.Exists(HostExePath))
        {
            AppLog.Write($"integration: enable rejected, host exe missing: {HostExePath}");
            return new Snapshot(State.Off, $"无法启用：缺少宿主程序 {Handoff.HostExeName}");
        }

        List<Action> undo = [];
        try
        {
            WriteHostJson();
            undo.Add(() => TryDeleteFile(HostJsonPath));

            SetDefaultValue(HostKeyPath, HostJsonPath);
            undo.Add(() => DeleteKey(HostKeyPath));

            WriteValue(ExtKeyPath, "path", CrxPath);
            WriteValue(ExtKeyPath, "version", crx.Version);
            undo.Add(() => DeleteKey(ExtKeyPath));

            WritePolicyEntries();
            undo.Add(DeletePolicyEntries);

            AppLog.Write($"integration: enabled (crx v{crx.Version}, exe dir {ExeDir})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"integration: enable FAILED: {ex.Message}, rolling back {undo.Count} item(s)");
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try { undo[i](); } catch (Exception ex2) { AppLog.Write($"integration: rollback step failed: {ex2.Message}"); }
            }
        }
        return Probe();
    }

    public static Snapshot Disable()
    {
        // 清单路径从注册表反查，不依赖本地记录：data\ 被删也能找到并删掉它
        string? hostJson = ReadDefaultValue(HostKeyPath);
        if (hostJson is { Length: > 0 })
        {
            if (IsOurHostJson(hostJson)) TryDeleteFile(hostJson);
            else AppLog.Write($"integration: skipped foreign host manifest {hostJson}");
        }

        // 扩展登记同理：只删指向本目录 crx 的那份，别人的原样留着
        if (ReadValue(ExtKeyPath, "path") is { } extPath)
        {
            if (SameDir(Path.GetDirectoryName(extPath) ?? "", ExeDir)) DeleteKey(ExtKeyPath);
            else AppLog.Write($"integration: skipped foreign extension entry {extPath}");
        }

        DeleteKey(HostKeyPath);
        DeletePolicyEntries();
        AppLog.Write("integration: disabled");
        return Probe();
    }

    // 启动自愈：只补自己缺的项；注册属于别的程序目录时不动，交给用户在设置里决定
    public static void RepairIfNeeded()
    {
        Snapshot snap = Probe();
        switch (snap.Status)
        {
            case State.Incomplete:
                AppLog.Write($"integration: repairing ({snap.Detail})");
                Enable();
                break;
            case State.OwnedElsewhere:
                AppLog.Write($"integration: left alone ({snap.Detail})");
                break;
        }
    }

    private sealed record CrxInfo(string Version, string Id);

    private static CrxInfo? ReadCrx()
    {
        try
        {
            using FileStream file = File.OpenRead(CrxPath);
            Span<byte> head = stackalloc byte[12];
            if (file.Read(head) != 12 || !head[..4].SequenceEqual("Cr24"u8)) return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) != 3) return null;
            file.Position = 12 + BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);

            // ZipArchive 按流起点算偏移，crx 的头会让本地头定位错位，所以先把 zip 段拷出来
            using MemoryStream zipBytes = new();
            file.CopyTo(zipBytes);
            zipBytes.Position = 0;
            using ZipArchive zip = new(zipBytes, ZipArchiveMode.Read);
            ZipArchiveEntry? entry = zip.GetEntry("manifest.json");
            if (entry is null) return null;
            using JsonDocument doc = JsonDocument.Parse(entry.Open());
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("version", out JsonElement v) || !root.TryGetProperty("key", out JsonElement k))
            {
                return null;
            }
            return new CrxInfo(v.GetString() ?? "", DeriveId(Convert.FromBase64String(k.GetString() ?? "")));
        }
        catch (Exception ex)
        {
            AppLog.Write($"integration: crx parse failed: {ex.Message}");
            return null;
        }
    }

    // 扩展 ID = SHA256(公钥 SPKI) 前 16 字节，每字节高低 4 位各映射一个 a~p 字母
    private static string DeriveId(byte[] spki)
    {
        byte[] hash = SHA256.HashData(spki);
        StringBuilder id = new(32);
        foreach (byte b in hash[..16])
        {
            id.Append((char)('a' + (b >> 4))).Append((char)('a' + (b & 15)));
        }
        return id.ToString();
    }

    private static void WriteHostJson()
    {
        string json = JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Downloader native messaging host",
            path = HostExePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{Handoff.ExtensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HostJsonPath, json);
    }

    private static bool IsOurHostJson(string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("name", out JsonElement name) && name.GetString() == HostName;
        }
        catch
        {
            return false;
        }
    }

    private static void WritePolicyEntries()
    {
        foreach (string path in PolicyKeyPaths)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path);
            if (key.GetValueNames().Any(n => key.GetValue(n) is string v && v == Handoff.ExtensionId)) continue;
            int slot = 1;
            while (key.GetValueNames().Contains(slot.ToString())) slot++;
            key.SetValue(slot.ToString(), Handoff.ExtensionId);
        }
    }

    private static void DeletePolicyEntries()
    {
        foreach (string path in PolicyKeyPaths)
        {
            bool empty = false;
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: true))
            {
                if (key is null) continue;
                // 只删值为我们扩展 ID 的条目，别人的策略原样留着
                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is string v && v == Handoff.ExtensionId) key.DeleteValue(name, throwOnMissingValue: false);
                }
                // 空壳子键要连键删掉，否则父键不为空、清不掉，Chrome 会一直显示"由贵单位管理"
                empty = key.GetValueNames().Length == 0 && key.GetSubKeyNames().Length == 0;
            }
            if (empty)
            {
                DeleteKey(path); // 句柄必须先关掉，开着删不掉自己
            }
        }
        // 向上清理空键：留着空壳会让 Chrome 一直显示"由贵单位管理"
        foreach (string path in new[] { @"Software\Policies\Google\Chrome", @"Software\Policies\Google" })
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null) continue;
            if (key.GetSubKeyNames().Length == 0 && key.GetValueNames().Length == 0)
            {
                DeleteKey(path);
            }
        }
    }

    private static bool HasOurAllowlistEntry(string path)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null && key.GetValueNames().Any(n => key.GetValue(n) is string v && v == Handoff.ExtensionId);
    }

    private static void SetDefaultValue(string keyPath, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("", value);
    }

    private static void WriteValue(string keyPath, string name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(name, value);
    }

    private static string? ReadDefaultValue(string keyPath) => ReadValue(keyPath, "");

    private static string? ReadValue(string keyPath, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(name) as string;
    }

    private static void DeleteKey(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            AppLog.Write($"integration: removed key {keyPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"integration: key delete failed: {keyPath}: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            AppLog.Write($"integration: removed file {path}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"integration: file delete failed: {path}: {ex.Message}");
        }
    }

    private static bool SameDir(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
