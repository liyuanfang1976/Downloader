namespace Downloader;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 多实例会互相覆盖 tasks.json 并争抢同一下载文件，只允许运行一个
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.WriteCrash("进程级未处理异常", ex);
            }
        };

        // 多实例会互相覆盖 tasks.json 并争抢同一下载文件，只允许运行一个
        using var mutex = new Mutex(true, Handoff.SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Downloader已在运行", "Downloader", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }    
}