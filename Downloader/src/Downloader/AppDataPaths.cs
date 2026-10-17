using System.Security.Principal;

namespace Downloader;

/// 本机状态（设置、任务列表、日志）的唯一路径来源。
/// 数据只落在 exe 同级的 data\ 下，因此 Debug、Release 与各个 checkout 天然互不干扰；
/// 删掉这个目录就是恢复出厂——程序不从任何别处读取或回填配置。
public static class AppDataPaths
{
    public static string Root { get; }

    /// 无法持久化时要告知用户的一句话；null 表示配置可以正常保存
    public static string? PersistenceWarning { get; }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string TaskFile => Path.Combine(Root, "tasks.json");
    public static string LogDir => Path.Combine(Root, "logs");
    public static string InboxDir => Path.Combine(Root, Handoff.InboxFolderName);

    static AppDataPaths()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        Root = Path.Combine(exeDir, "data");

        // Program Files 一类的受保护目录里，未提权的写入会被 VirtualStore 悄悄重定向到
        // %LOCALAPPDATA%\VirtualStore，探针照样"成功"，所以必须在探针之前就判定
        if (IsProtectedDir(exeDir) && !IsElevated)
        {
            PersistenceWarning = "当前程序目录需要管理员权限才能保存配置文件";
            return;
        }

        try
        {
            Directory.CreateDirectory(Root);
            string probe = Path.Combine(Root, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            PersistenceWarning = "当前程序目录需要管理员权限才能保存配置文件";
        }
        catch (Exception ex)
        {
            PersistenceWarning = $"配置目录 data 无法使用（{ex.Message}），本次运行的设置与任务列表不会被保存";
        }
    }

    static bool IsProtectedDir(string exeDir)
    {
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        })
        {
            if (root.Length > 0
                && exeDir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
