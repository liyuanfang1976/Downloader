using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Downloader;

internal static class Program
{
    private const int MaxMessageBytes = 1024 * 1024;

    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    private static string InboxDir => Path.Combine(DataDir, Handoff.InboxFolderName);
    private static string LogPath => Path.Combine(DataDir, "logs", "native-host.log");

    private static int Main()
    {
        try
        {
            Run();
            return 0;
        }
        catch (Exception ex)
        {
            TryLog(ex.ToString());
            return 1;
        }
    }

    private static void Run()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();

        while (Read(input) is { } json)
        {
            bool ok = HandoffTask.TryParse(json, out _) is { Url.Length: > 0 } handoff && Deliver(handoff);
            Write(output, JsonSerializer.Serialize(new { ok }));
        }
    }

    private static bool Deliver(HandoffTask handoff)
    {
        try
        {
            Directory.CreateDirectory(InboxDir);
            // 临时文件落在收件箱外：收件箱里任何时候都只有完整的成品，不会被读到半截
            string temp = Path.Combine(DataDir, $"{Guid.NewGuid():N}.json.tmp");
            string target = Path.Combine(InboxDir, $"{Guid.NewGuid():N}.json");
            File.WriteAllText(temp, handoff.ToJson());
            File.Move(temp, target, overwrite: true);
            TryLog($"handoff delivered: {handoff.Url} → {Path.GetFileName(target)}");
            EnsureAppRunning();
            return true;
        }
        catch (Exception ex)
        {
            TryLog(ex.ToString());
            return false;
        }
    }

    private static void EnsureAppRunning()
    {
        if (Mutex.TryOpenExisting(Handoff.SingleInstanceMutexName, out _))
        {
            return;
        }

        string exe = Path.Combine(AppContext.BaseDirectory, Handoff.AppExeName);
        if (File.Exists(exe))
        {
            TryLog($"app not running, launching {Handoff.AppExeName}");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = AppContext.BaseDirectory });
        }
        else
        {
            TryLog($"app exe not found: {exe}");
        }
    }

    private static string? Read(Stream input)
    {
        byte[] header = new byte[4];
        try
        {
            input.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            TryLog($"invalid message length: {length}, closing stream");
            return null;
        }

        byte[] body = new byte[length];
        input.ReadExactly(body);
        return Encoding.UTF8.GetString(body);
    }

    private static void Write(Stream output, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        output.Write(header);
        output.Write(body);
        output.Flush();
    }

    private static void TryLog(string text)
    {
        try
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n");
        }
        catch
        {
        }
    }
}
