using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Downloader;

public class MainForm : Form
{
    private readonly DownloadService _downloadService = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly Dictionary<Guid, ListViewItem> _listItems = new();
    private readonly Dictionary<Guid, Task> _runningTasks = new();
    private readonly List<Guid> _pendingQueue = new();

    private AppSettings _settings = null!;
    private readonly List<string> _startupNotices = [];
    private TaskInbox? _inbox;
    private readonly List<HandoffTask> _pendingHandoffs = [];
    private bool _handoffDialogOpen;

    private ToolStrip _toolbar = null!;
    private ToolStripButton _btnNewTask = null!;
    private ToolStripButton _btnStart = null!;
    private ToolStripButton _btnPause = null!;
    private ToolStripButton _btnDelete = null!;
    private ListView _taskList = null!;
    private ToolStripMenuItem _menuStart = null!;
    private ToolStripMenuItem _menuPause = null!;
    private ToolStripMenuItem _menuDelete = null!;
    private ToolStripMenuItem _menuOpenFolder = null!;
    private ToolStripMenuItem _menuOpenFile = null!;
    private ToolStripMenuItem _menuCopyUrl = null!;
    private ToolStripMenuItem _menuProperties = null!;
    private Panel _detailPanel = null!;
    private Label _lblUrl = null!;
    private Label _lblSavePath = null!;
    private Label _lblProgress = null!;
    private Label _lblSha256 = null!;
    private Label _lblResume = null!;
    private Button _btnComputeSha = null!;
    private StatusStrip _statusBar = null!;
    private ToolStripStatusLabel _statusText = null!;

    public MainForm()
    {
        _settings = SettingsStore.Load(out string? settingsError);
        if (settingsError is not null)
        {
            _startupNotices.Add(settingsError);
        }

        ApplySettings();
        InitializeComponent();
        LoadSavedTasks();

        int withReferer = _listItems.Values.Count(i => i.Tag is DownloadTask t && t.Referer != null);
        AppLog.Write($"startup: {_settings.Describe()}, tasks={_listItems.Count}" +
            (withReferer > 0 ? $" ({withReferer} with referer)" : ""));
        foreach (string notice in _startupNotices)
        {
            AppLog.Write($"startup notice: {notice}");
        }

        if (_startupNotices.Count > 0)
        {
            _statusText.Text = string.Join("；", _startupNotices);
        }
        FormClosing += OnFormClosing;
    }

    private void StartInbox()
    {
        _inbox = new TaskInbox(handoff => BeginInvoke(() => AcceptHandoff(handoff)));
        _inbox.Drain();
    }

    private void AcceptHandoff(HandoffTask handoff)
    {
        AppLog.Write($"handoff received: {handoff.Url}");
        _pendingHandoffs.Add(handoff);

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
        ShowHandoffDialogs();
    }

    private void ShowHandoffDialogs()
    {
        if (_handoffDialogOpen)
        {
            return;
        }

        _handoffDialogOpen = true;
        try
        {
            while (_pendingHandoffs.Count > 0)
            {
                string origin = OriginOf(_pendingHandoffs[0].Url);
                var batch = _pendingHandoffs.Where(h => OriginOf(h.Url) == origin).ToList();
                _pendingHandoffs.RemoveAll(h => OriginOf(h.Url) == origin);
                ConfirmHandoff(batch);
            }
        }
        finally
        {
            _handoffDialogOpen = false;
        }
    }

    // 同一站点的多个下载合并到一次弹窗里确认，避免连投时弹出一串窗口
    private void ConfirmHandoff(List<HandoffTask> batch)
    {
        string initialDir = batch
            .Select(h => h.SaveDir)
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? _settings.DefaultSaveDir;

        ShowAddTaskDialog(
            initialDir,
            string.Join(Environment.NewLine, batch.Select(h => h.Url)),
            batch[0].Headers);
    }

    // 添加链接弹窗的统一出口。边下边弹窗开启时，探测就绪的行由 TryAutoStart 抢跑建单；
    // 用户点"立即下载"则保留已开跑的、补齐剩下的；关窗（×/Esc）即取消——
    // 未确认的自启单一律删除（含已下完的和其文件），不留手动清理的活
    private void ShowAddTaskDialog(string initialDir, string? prefilledUrls, Dictionary<string, string>? headers)
    {
        var autoStarted = new List<DownloadTask>();
        using var dialog = new AddTaskDialog(
            (url, h, ct) => _downloadService.ProbeAsync(url, ct, h),
            initialDir, prefilledUrls, headers);
        dialog.OnRowReady = row =>
        {
            var task = TryAutoStart(row, dialog.SaveDir, headers);
            if (task != null)
            {
                autoStarted.Add(task);
            }
            return task;
        };
        dialog.OnTaskAbort = task =>
        {
            autoStarted.Remove(task);
            AbortAutoStarted(task, "row unchecked");
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            // 批量时的汇总文案由 AcceptConfirmedTasks 统一给
            AcceptConfirmedTasks(dialog, headers);
            return;
        }

        foreach (var task in autoStarted)
        {
            AbortAutoStarted(task, "dialog cancelled");
        }
    }

    // 边下边弹窗的抢跑门控：设置开着、目录已选好，且要么列表里没有同址任务，
    // 要么重复链接策略就是编号副本（新建单不冲突）。其余情况不抢跑——
    // "续传/覆盖/跳过"都需要用户先决策，否则会和已经开跑的新单打架
    private DownloadTask? TryAutoStart(AddTaskDialog.ReadyRow row, string saveDir, Dictionary<string, string>? headers)
    {
        if (!_settings.StartDownloadWhileDialogOpen || saveDir.Length == 0 || !Directory.Exists(saveDir))
        {
            return null;
        }

        var existing = FindTaskByUrl(row.Url);
        if (existing != null && _settings.DuplicateLinkPolicy != DuplicateLinkPolicy.NewCopy)
        {
            AppLog.Write($"auto-start skipped (duplicate): {row.Url} → existing \"{existing.FileName}\"");
            return null;
        }

        var task = new DownloadTask
        {
            Url = row.Url,
            SaveDir = saveDir,
            // 与 Submit 同一套改名规则：磁盘上已有同名文件就编号
            FileName = Utils.GetUniqueFileName(saveDir, row.FileName),
            TotalBytes = row.Info?.TotalBytes ?? -1,
            AcceptRanges = row.Info?.AcceptRanges ?? false,
            ETag = row.Info?.ETag,
            LastModified = row.Info?.LastModified,
            Probed = true,
            RequestHeaders = MergeReferer(headers, row.Referer),
        };
        AddTaskRow(task);
        EnqueueTask(task);
        AppLog.Write($"auto-start: \"{task.FileName}\" (边下边弹窗)");
        return task;
    }

    // 删除一个未确认的自启单：在跑的走取消+删文件（与点删除同一机制），
    // 已下完/排队中的直接删文件移出列表
    private void AbortAutoStarted(DownloadTask task, string reason)
    {
        AppLog.Write($"auto-start aborted: \"{task.FileName}\" ({reason})");
        if (_runningTasks.ContainsKey(task.Id))
        {
            task.DeleteRequested = true;
            if (_cancellations.TryGetValue(task.Id, out var cts))
            {
                cts.Cancel();
            }
            // 引擎收尾时会自己删文件；万一没删掉（被杀毒软件锁住），3 秒后补刀。
            // 等到不在跑了再查，避免和引擎的删除撞车
            RunLater(3000, () =>
            {
                if (!_runningTasks.ContainsKey(task.Id) && File.Exists(task.FilePath))
                {
                    CleanupTaskFiles(task);
                }
            });
        }
        else
        {
            CleanupTaskFiles(task);
        }

        _cancellations.Remove(task.Id);
        _pendingQueue.Remove(task.Id);
        if (_listItems.Remove(task.Id, out var item))
        {
            _taskList.Items.Remove(item);
        }
        UpdateToolbarButtons();
        SaveTaskList();
    }

    // 删除自启单的文件。刚下完（或刚打断）的大文件可能被杀毒软件扫描锁着，
    // File.Delete 会静默失败——那等于把"删干净"的承诺变成谎言，用户又得手动扫垃圾。
    // 删不掉就 2 秒后重试一次并留痕，再失败也在 trace.log 里写明路径
    private void CleanupTaskFiles(DownloadTask task)
    {
        Utils.TryDeleteFile(task.FilePath);
        Utils.TryDeleteFile(Utils.StatePathFor(task));
        if (!File.Exists(task.FilePath))
        {
            return;
        }

        AppLog.Write($"file cleanup deferred: \"{task.FileName}\" 被占用，2 秒后重试");
        RunLater(2000, () =>
        {
            Utils.TryDeleteFile(task.FilePath);
            if (File.Exists(task.FilePath))
            {
                AppLog.Write($"file cleanup FAILED: \"{task.FileName}\" — {task.FilePath}");
            }
        });
    }

    // 行级 Referer 覆盖批级带头（浏览器接管带来的那套）：抢跑建单和确认建单共用
    private static Dictionary<string, string>? MergeReferer(Dictionary<string, string>? headers, string? referer)
        => referer is { } r ? Utils.WithHeader(headers, "Referer", r) : headers;

    // 延后到 UI 线程执行一次：用于"等文件锁释放再删"这类重试，Tick 即焚
    private static void RunLater(int delayMs, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = delayMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    private static string OriginOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : url;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 接管缺项时顺手补齐；属于别的程序目录的注册不动，等用户在设置里决定
        BrowserIntegration.RepairIfNeeded();
        // 收件箱的补收必须等窗口就绪：宿主冷启动拉起进程时单子已经躺在收件箱里，
        // 而构造函数阶段窗口句柄还没创建，BeginInvoke 会抛异常并把单子弄丢
        StartInbox();
        // 放在首次绘制之后：弹窗有正确的 owner，用户同时也看得见"列表是空的"这件事
        if (AppDataPaths.PersistenceWarning is string warning)
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "Downloader",
                Text = warning,
                Icon = TaskDialogIcon.Warning,
                Buttons = { new TaskDialogButton("知道了") },
            });
        }
    }

    private void ApplySettings()
    {
        _downloadService.SegmentsPerFile = _settings.SegmentsPerFile;
        _downloadService.SpeedLimitBytesPerSec = _settings.SpeedLimitBytesPerSec;
        _downloadService.SkipResumeIdentityCheck = _settings.SkipResumeIdentityCheck;
        AppLog.Sink = _settings.EnableDebugLog ? AppendTraceFile : null;
    }

    private int MaxConcurrentDownloads => _settings.MaxConcurrentDownloads;

    // trace.log 的落盘点。引擎后台线程与界面线程都会写，加锁防止并发 AppendAllText
    // 互相撞 sharing violation 把行 silently 丢掉；写失败不影响主流程
    private static readonly object TraceWriteLock = new();

    private static void AppendTraceFile(string message)
    {
        lock (TraceWriteLock)
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.LogDir);
                File.AppendAllText(Path.Combine(AppDataPaths.LogDir, "trace.log"), message + "\n");
            }
            catch
            {
            }
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _inbox?.Dispose();
        // 取消所有任务后不等待：分片进度已定期落盘，重启后可断点续传
        if (_cancellations.Count > 0)
        {
            AppLog.Write($"shutdown: cancelling {_cancellations.Count} running task(s)");
        }
        foreach (var cts in _cancellations.Values)
        {
            cts.Cancel();
        }
        // 关窗时同步写，避免进程退出前后台保存来不及完成
        SaveTaskList(synchronous: true);
    }

    private void InitializeComponent()
    {
        Text = "Downloader";
        Size = new Size(960, 640);
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;

        BuildToolbar();
        BuildTaskList();
        BuildDetailPanel();
        BuildStatusBar();

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // 要放得下"续传: …"那一行（y=104 起，高 18），比原来多留 20px
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));

        Controls.Add(table);
        table.Controls.Add(_toolbar, 0, 0);
        table.Controls.Add(_taskList, 0, 1);
        table.Controls.Add(_detailPanel, 0, 2);

        // 让 Fill 表格排在状态栏之前，避免表格铺满整个客户区盖住底部状态栏
        table.BringToFront();
    }

    private void BuildToolbar()
    {
        _toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(4, 2, 4, 2),
        };

        _btnNewTask = new ToolStripButton("新建任务") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _btnNewTask.Click += OnNewTaskClick;

        _btnStart = new ToolStripButton("开始") { Enabled = false };
        _btnStart.Click += OnStartClick;
        _btnPause = new ToolStripButton("暂停") { Enabled = false };
        _btnPause.Click += OnPauseClick;
        _btnDelete = new ToolStripButton("删除") { Enabled = false };
        _btnDelete.Click += OnDeleteClick;

        var btnStartAll = new ToolStripButton("全部开始");
        btnStartAll.Click += OnStartAllClick;
        var btnPauseAll = new ToolStripButton("全部暂停");
        btnPauseAll.Click += OnPauseAllClick;
        var btnSettings = new ToolStripButton("设置");
        btnSettings.Click += OnSettingsClick;

        _toolbar.Items.Add(_btnNewTask);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_btnStart);
        _toolbar.Items.Add(_btnPause);
        _toolbar.Items.Add(_btnDelete);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(btnStartAll);
        _toolbar.Items.Add(btnPauseAll);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(btnSettings);
    }

    private void OnSettingsClick(object? sender, EventArgs e)
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings.MaxConcurrentDownloads = dialog.MaxConcurrent;
        _settings.SegmentsPerFile = dialog.Segments;
        _settings.DefaultSaveDir = dialog.SaveDir;
        _settings.EnableDebugLog = dialog.DebugLog;
        _settings.SkipResumeIdentityCheck = dialog.SkipResumeCheck;
        _settings.DuplicateLinkPolicy = dialog.DuplicateLink;
        _settings.StartDownloadWhileDialogOpen = dialog.StartDownloadWhileDialogOpen;
        _settings.SpeedLimitBytesPerSec = dialog.SpeedLimitBytesPerSec;
        SettingsStore.Save(_settings);
        ApplySettings();
        StartNextQueued();
        _statusText.Text = "设置已保存";

        if (dialog.BrowserWanted != dialog.BrowserWasOn)
        {
            ApplyBrowserIntegration(dialog.BrowserWanted);
        }
    }

    private void ApplyBrowserIntegration(bool wanted)
    {
        BrowserIntegration.Snapshot snap = wanted ? BrowserIntegration.Enable() : BrowserIntegration.Disable();
        bool done = snap.Status is BrowserIntegration.State.Registered or BrowserIntegration.State.Off;
        string text = snap.Status switch
        {
            BrowserIntegration.State.Registered =>
                "已注册。请完全退出并重启 Chrome，首次会问你是否启用该扩展。",
            // 删除外部扩展的注册表条目本身就是 Chrome 规定的卸载方式，它会自己移除该扩展
            BrowserIntegration.State.Off when !wanted =>
                "已撤回本机注册。Chrome 重启后会自动移除该扩展；若仍在，可在扩展页手动删除。",
            BrowserIntegration.State.Off => "未能启用：" + snap.Detail,
            _ => "未彻底完成：" + snap.Detail,
        };
        MessageBox.Show(this, text, "下载接管", MessageBoxButtons.OK,
            done ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        _statusText.Text = $"下载接管：{snap.Detail}";
    }

    private void OnStartAllClick(object? sender, EventArgs e)
    {
        int count = 0;
        foreach (ListViewItem item in _taskList.Items)
        {
            if (item.Tag is DownloadTask task
                && task.Status is DownloadStatus.Paused or DownloadStatus.Error)
            {
                EnqueueTask(task);
                count++;
            }
        }
        _statusText.Text = count > 0 ? $"已开始 {count} 个任务" : "没有可开始的任务";
    }

    private void OnPauseAllClick(object? sender, EventArgs e)
    {
        int count = 0;
        foreach (ListViewItem item in _taskList.Items)
        {
            if (item.Tag is not DownloadTask task)
            {
                continue;
            }

            if (task.Status == DownloadStatus.Waiting && _pendingQueue.Contains(task.Id))
            {
                _pendingQueue.Remove(task.Id);
                task.Status = DownloadStatus.Paused;
                UpdateTaskRow(task);
                count++;
            }
            else if (task.Status == DownloadStatus.Downloading
                && _cancellations.TryGetValue(task.Id, out var cts))
            {
                task.Status = DownloadStatus.Paused;
                cts.Cancel();
                UpdateTaskRow(task);
                count++;
            }
        }
        _statusText.Text = count > 0 ? $"已暂停 {count} 个任务" : "没有进行中的任务";
    }

    private void BuildTaskList()
    {
        _taskList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            GridLines = true,
        };

        _taskList.Columns.Add("文件名", 260);
        _taskList.Columns.Add("大小", 100, HorizontalAlignment.Right);
        _taskList.Columns.Add("进度", 120);
        _taskList.Columns.Add("速度", 100, HorizontalAlignment.Right);
        _taskList.Columns.Add("状态", 90);
        _taskList.Columns.Add("URL", 300);
        _taskList.ShowItemToolTips = true;

        // 速度列 100ms 级高频刷新，双缓冲避免重绘闪烁（属性是 protected，用反射打开）
        typeof(ListView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(_taskList, true);

        _taskList.SelectedIndexChanged += OnTaskSelectionChanged;
        _taskList.MouseDown += OnTaskListMouseDown;
        _taskList.ContextMenuStrip = BuildTaskContextMenu();
    }

    // 右键行为与资源管理器一致：点中未选中的行则只选中它，
    // 点中已选中的行（含多选）则保留原选择，方便对多选整体操作
    private void OnTaskListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }
        var item = _taskList.GetItemAt(e.X, e.Y);
        if (item == null || _taskList.SelectedItems.Contains(item))
        {
            return;
        }
        _taskList.SelectedItems.Clear();
        item.Selected = true;
        item.Focused = true;
    }

    private ContextMenuStrip BuildTaskContextMenu()
    {
        _menuStart = new ToolStripMenuItem("开始", null, OnStartClick);
        _menuPause = new ToolStripMenuItem("暂停", null, OnPauseClick);
        _menuDelete = new ToolStripMenuItem("删除", null, OnDeleteClick);
        _menuOpenFolder = new ToolStripMenuItem("打开文件夹", null, OnOpenFolderClick);
        _menuOpenFile = new ToolStripMenuItem("打开文件", null, OnOpenFileClick);
        _menuCopyUrl = new ToolStripMenuItem("复制链接", null, OnCopyUrlClick);
        _menuProperties = new ToolStripMenuItem("属性", null, OnTaskPropertiesClick);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _menuStart, _menuPause, _menuDelete,
            new ToolStripSeparator(),
            _menuOpenFolder, _menuOpenFile, _menuCopyUrl,
            new ToolStripSeparator(),
            _menuProperties,
        });
        menu.Opening += (_, e) =>
        {
            var selected = GetSelectedTasks();
            _menuStart.Enabled = selected.Any(t => t.Status is DownloadStatus.Paused or DownloadStatus.Error);
            _menuPause.Enabled = selected.Any(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Waiting);
            _menuDelete.Enabled = selected.Count > 0;
            _menuOpenFolder.Enabled = selected.Count > 0;
            _menuCopyUrl.Enabled = selected.Count > 0;
            _menuOpenFile.Enabled = selected.Count == 1
                && selected[0].Status == DownloadStatus.Completed
                && File.Exists(selected[0].FilePath);
            _menuProperties.Enabled = selected.Count == 1;
        };
        return menu;
    }

    private void OnTaskPropertiesClick(object? sender, EventArgs e)
    {
        if (_taskList.SelectedItems.Count != 1 || _taskList.SelectedItems[0].Tag is not DownloadTask task)
        {
            return;
        }

        using var dialog = new TaskPropertiesDialog(task);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        task.Referer = dialog.Referer;
        AppLog.Write(task.Referer is { } r
            ? $"referer set: \"{task.FileName}\" → {r}"
            : $"referer cleared: \"{task.FileName}\"");
        UpdateTaskRow(task);
        SaveTaskList();
    }

    private List<DownloadTask> GetSelectedTasks() => _taskList.SelectedItems
        .Cast<ListViewItem>()
        .Select(i => i.Tag)
        .OfType<DownloadTask>()
        .ToList();

    private void OnOpenFolderClick(object? sender, EventArgs e)
    {
        var task = GetSelectedTasks().FirstOrDefault();
        if (task == null)
        {
            return;
        }
        try
        {
            // 只高亮已完成任务的成品文件；下载中的文件是预分配的半成品，
            // 高亮它只会诱导用户点开一个不可用的文件
            if (task.Status == DownloadStatus.Completed && File.Exists(task.FilePath))
            {
                // /select 定位并高亮文件本身，比单纯打开目录更直观
                Process.Start("explorer.exe", $"/select,\"{task.FilePath}\"");
            }
            else if (Directory.Exists(task.SaveDir))
            {
                Process.Start("explorer.exe", task.SaveDir);
            }
            else
            {
                _statusText.Text = "文件夹不存在";
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"打开文件夹失败: {ex.Message}";
        }
    }

    private void OnOpenFileClick(object? sender, EventArgs e)
    {
        var task = GetSelectedTasks().FirstOrDefault();
        if (task == null || !File.Exists(task.FilePath))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(task.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _statusText.Text = $"打开文件失败: {ex.Message}";
        }
    }

    private void OnCopyUrlClick(object? sender, EventArgs e)
    {
        var urls = GetSelectedTasks().Select(t => t.Url).ToList();
        if (urls.Count == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(string.Join("\r\n", urls));
            _statusText.Text = urls.Count == 1 ? "链接已复制" : $"已复制 {urls.Count} 个链接";
        }
        catch
        {
            // 剪贴板被占用等情况忽略
        }
    }

    private void BuildDetailPanel()
    {
        _detailPanel = new Panel { Dock = DockStyle.Fill };

        _lblUrl = new Label { Text = "URL: -", Location = new Point(10, 8), AutoEllipsis = true };
        _lblSavePath = new Label { Text = "保存路径: -", Location = new Point(10, 30), AutoEllipsis = true };
        _lblProgress = new Label { Text = "进度: -", Location = new Point(10, 52), AutoEllipsis = true };
        _lblSha256 = new Label
        {
            Text = "SHA256: -",
            Location = new Point(10, 76),
            Font = new Font("Consolas", 9f),
            AutoEllipsis = true,
        };
        _btnComputeSha = new Button { Text = "计算/复制 SHA256", Size = new Size(150, 26), Enabled = false };
        _btnComputeSha.Click += OnComputeShaClick;

        _lblResume = new Label
        {
            Text = "",
            Location = new Point(10, 104),
            AutoEllipsis = true,
            Visible = false,
        };

        _detailPanel.Controls.Add(_lblUrl);
        _detailPanel.Controls.Add(_lblSavePath);
        _detailPanel.Controls.Add(_lblProgress);
        _detailPanel.Controls.Add(_lblSha256);
        _detailPanel.Controls.Add(_lblResume);
        _detailPanel.Controls.Add(_btnComputeSha);

        // Anchor 的锚定距离在控件定位时按父容器当前大小计算，
        // 构造期间面板尚未布局，会得到错误的边距，因此手动在每次布局时定位
        _detailPanel.Resize += (_, _) => LayoutDetailPanel();
        LayoutDetailPanel();
    }

    private void LayoutDetailPanel()
    {
        if (_detailPanel.Width <= 0)
        {
            return;
        }

        int right = _detailPanel.Width - 16;
        _btnComputeSha.Location = new Point(right - _btnComputeSha.Width, 72);
        int labelWidth = _btnComputeSha.Left - 10 - 8;
        _lblUrl.Size = new Size(labelWidth, 18);
        _lblSavePath.Size = new Size(labelWidth, 18);
        _lblProgress.Size = new Size(labelWidth, 18);
        _lblSha256.Size = new Size(Math.Max(labelWidth, 100), 18);
        _lblResume.Size = new Size(_detailPanel.Width - 20, 18);
    }

    private async void OnComputeShaClick(object? sender, EventArgs e)
    {
        if (_taskList.SelectedItems.Count == 0
            || _taskList.SelectedItems[0].Tag is not DownloadTask task)
        {
            return;
        }

        _btnComputeSha.Enabled = false;
        _lblSha256.Text = "SHA256: 计算中…";
        try
        {
            string path = task.FilePath;
            string hash = await Task.Run(() => Utils.ComputeSha256(path));
            task.ActualSha256 = hash;
            try { Clipboard.SetText(hash); } catch { }
        }
        catch (Exception ex)
        {
            _lblSha256.Text = $"SHA256: 计算失败 {ex.Message}";
            return;
        }
        UpdateDetailLabels(task);
    }

    private void BuildStatusBar()
    {
        _statusBar = new StatusStrip();
        _statusText = new ToolStripStatusLabel("就绪");
        _statusBar.Items.Add(_statusText);
        Controls.Add(_statusBar);
    }

    private void LoadSavedTasks()
    {
        var records = TaskStore.Load(out string? tasksError);
        if (tasksError is not null)
        {
            _startupNotices.Add(tasksError);
        }

        foreach (var record in records)
        {
            var task = new DownloadTask
            {
                Url = record.Url,
                SaveDir = record.SaveDir,
                FileName = record.FileName,
                TotalBytes = record.TotalBytes,
                ExpectedSha256 = record.Sha256,
            };

            if (!Enum.TryParse<DownloadStatus>(record.Status, out var status)
                || status is DownloadStatus.Downloading or DownloadStatus.Waiting or DownloadStatus.Retrying)
            {
                status = DownloadStatus.Paused;
            }
            task.Status = status;

            if (status == DownloadStatus.Completed)
            {
                task.ReceivedBytes = task.TotalBytes;
            }
            else
            {
                TryRestoreReceivedFromState(task);
            }

            // 重启后恢复 Referer（tasks.json 里唯一持久化的请求头）；
            // Cookie 等凭据类头按设计不落盘，重启后需重新经浏览器下载获得
            task.Referer = record.Referer;

            AddTaskRow(task);
        }
    }

    private static void TryRestoreReceivedFromState(DownloadTask task)
    {
        try
        {
            string statePath = Utils.StatePathFor(task);
            if (!File.Exists(statePath))
            {
                return;
            }
            var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(statePath));
            if (state != null)
            {
                task.ReceivedBytes = state.Segments.Sum(s => s.Downloaded);
            }
        }
        catch
        {
            // 状态文件损坏时按 0 处理
        }
    }

    private void OnNewTaskClick(object? sender, EventArgs e)
    {
        ShowAddTaskDialog(_settings.DefaultSaveDir, null, null);
    }

    // 弹窗确认过的任务统一在这里建单；headers 来自浏览器接管，手动新建时为 null
    private void AcceptConfirmedTasks(AddTaskDialog dialog, Dictionary<string, string>? headers)
    {
        // 弹窗里改过的目录就是下一次想用的目录，直接记为"上次使用"，省得再去设置页
        if (!string.Equals(dialog.SaveDir, _settings.DefaultSaveDir, StringComparison.OrdinalIgnoreCase))
        {
            _settings.DefaultSaveDir = dialog.SaveDir;
            SettingsStore.Save(_settings);
        }

        int created = 0, reused = 0, skipped = 0;
        string? note = null;
        foreach (var confirmed in dialog.ConfirmedTasks)
        {
            if (confirmed.Started != null)
            {
                // 边下边弹窗已提前建单，确认只是认账
                created++;
                continue;
            }

            var existing = FindTaskByUrl(confirmed.Url);
            if (existing == null)
            {
                CreateTask(confirmed, dialog.SaveDir, headers);
                created++;
                continue;
            }

            AppLog.Write($"duplicate link: {confirmed.Url} → existing \"{existing.FileName}\" ({Utils.DescribeState(existing)})");
            var policy = _settings.DuplicateLinkPolicy;
            if (policy == DuplicateLinkPolicy.Ask)
            {
                using var picker = new DuplicateLinkDialog(existing, confirmed.FileName);
                if (picker.ShowDialog(this) != DialogResult.OK)
                {
                    AppLog.Write("duplicate policy: picker cancelled by user");
                    skipped++;
                    continue;
                }
                policy = picker.Selected;
                AppLog.Write($"duplicate policy: {policy} chosen by user, remembered={picker.Remember}");
                if (picker.Remember)
                {
                    _settings.DuplicateLinkPolicy = policy;
                    SettingsStore.Save(_settings);
                }
            }
            else
            {
                AppLog.Write($"duplicate policy: {policy} (from settings)");
            }

            switch (policy)
            {
                case DuplicateLinkPolicy.Overwrite:
                    RestartForOverwrite(existing);
                    note = $"已重新下载: {existing.FileName}";
                    reused++;
                    break;
                case DuplicateLinkPolicy.ResumeOrComplete:
                    note = ReuseOrNotify(existing);
                    reused++;
                    break;
                case DuplicateLinkPolicy.Skip:
                    AppLog.Write($"duplicate action: skipped \"{existing.FileName}\"");
                    note = $"已跳过: {existing.FileName}";
                    skipped++;
                    break;
                default:
                    CreateTask(confirmed, dialog.SaveDir, headers);
                    AppLog.Write($"duplicate action: created copy \"{confirmed.FileName}\"");
                    created++;
                    break;
            }
        }

        SaveTaskList();
        // 单个动作保留具体文案；一批多个时只给汇总，否则只看到最后一个动作的结果
        if (dialog.ConfirmedTasks.Count > 1)
        {
            var parts = new List<string>();
            if (created > 0) parts.Add($"新建 {created}");
            if (reused > 0) parts.Add($"沿用已有 {reused}");
            if (skipped > 0) parts.Add($"跳过 {skipped}");
            _statusText.Text = parts.Count > 0 ? $"已处理: {string.Join("，", parts)}" : "没有添加任何任务";
        }
        else if (note != null)
        {
            _statusText.Text = note;
        }
    }

    // 按地址匹配已有任务，忽略 query：签名直链每次刷新都长一个样，逐字比对等于
    // 让"浏览器重复下载同一文件"永远查不出来。与续传校验 SameRemoteUrl 同口径
    private DownloadTask? FindTaskByUrl(string url)
    {
        string identity = DownloadService.UrlIdentity(url);
        foreach (var item in _listItems.Values)
        {
            if (item.Tag is DownloadTask task
                && string.Equals(DownloadService.UrlIdentity(task.Url), identity, StringComparison.Ordinal))
            {
                return task;
            }
        }
        return null;
    }

    private void CreateTask(AddTaskDialog.ConfirmedTask confirmed, string saveDir, Dictionary<string, string>? headers)
    {
        var task = new DownloadTask
        {
            Url = confirmed.Url,
            SaveDir = saveDir,
            FileName = confirmed.FileName,
            TotalBytes = confirmed.Info.TotalBytes,
            AcceptRanges = confirmed.Info.AcceptRanges,
            ETag = confirmed.Info.ETag,
            LastModified = confirmed.Info.LastModified,
            Probed = true,
            // 弹窗里行级填的 Referer 覆盖批级带头（浏览器接管的那套）
            RequestHeaders = MergeReferer(headers, confirmed.Referer),
        };
        AddTaskRow(task);
        EnqueueTask(task);
    }

    // 覆盖 = 把已有任务打回重下。断点文件必须先删，否则引擎会接着旧进度续，覆盖就变成了续传；
    // 正在下载的任务先取消，收尾后由 RunTaskAsync 的 finally 调用本方法重启，
    // 避免两个下载同时写同一个文件
    private void RestartForOverwrite(DownloadTask task)
    {
        task.RestartRequested = false;
        if (_runningTasks.ContainsKey(task.Id))
        {
            task.RestartRequested = true;
            if (_cancellations.TryGetValue(task.Id, out var cts))
            {
                cts.Cancel();
            }
            AppLog.Write($"duplicate action: \"{task.FileName}\" is running, restart deferred until unwind");
            return;
        }

        _pendingQueue.Remove(task.Id);
        Utils.TryDeleteFile(Utils.StatePathFor(task));
        task.Status = DownloadStatus.Paused;
        task.ErrorMessage = null;
        task.ResumeNote = null;
        task.ReceivedBytes = 0;
        UpdateTaskRow(task);
        EnqueueTask(task);
        AppLog.Write($"duplicate action: restarting \"{task.FileName}\" from scratch (dtask removed)");
    }

    // 策略"续传或完成已有任务"：下完了就告知，正在下就保持原样，其余打回队列让引擎自己判断
    private string ReuseOrNotify(DownloadTask task)
    {
        if (task.Status == DownloadStatus.Completed && File.Exists(task.FilePath))
        {
            AppLog.Write($"duplicate action: \"{task.FileName}\" already completed with file present, notify only");
            return $"{task.FileName} 已在列表中完成，未重复下载";
        }
        if (_runningTasks.ContainsKey(task.Id) || _pendingQueue.Contains(task.Id))
        {
            AppLog.Write($"duplicate action: \"{task.FileName}\" already in progress, notify only");
            return $"{task.FileName} 正在下载中，未重复添加";
        }

        bool hasBreakpoint = File.Exists(Utils.StatePathFor(task));
        if (task.Status == DownloadStatus.Completed)
        {
            task.ReceivedBytes = 0; // 原文件不见了，旧的完成度不作数
        }
        task.Status = DownloadStatus.Paused;
        task.ErrorMessage = null;
        task.ResumeNote = null;
        UpdateTaskRow(task);
        EnqueueTask(task);
        AppLog.Write($"duplicate action: re-enqueued \"{task.FileName}\" ({(hasBreakpoint ? "with breakpoint" : "no breakpoint, fresh download")})");
        return hasBreakpoint ? $"已续传: {task.FileName}" : $"已重新下载: {task.FileName}";
    }

    private void EnqueueTask(DownloadTask task)
    {
        if (task.Status == DownloadStatus.Completed
            || _runningTasks.ContainsKey(task.Id)
            || _pendingQueue.Contains(task.Id))
        {
            return;
        }

        if (_runningTasks.Count < MaxConcurrentDownloads)
        {
            AppLog.Write($"enqueue {task.FileName}: slot free ({_runningTasks.Count}/{MaxConcurrentDownloads}) → start");
            StartTask(task);
        }
        else
        {
            AppLog.Write($"enqueue {task.FileName}: queue full ({_runningTasks.Count}/{MaxConcurrentDownloads}) → queued");
            task.Status = DownloadStatus.Waiting;
            UpdateTaskRow(task);
            _pendingQueue.Add(task.Id);
        }
    }

    private void StartNextQueued()
    {
        while (_pendingQueue.Count > 0 && _runningTasks.Count < MaxConcurrentDownloads)
        {
            Guid nextId = _pendingQueue[0];
            _pendingQueue.RemoveAt(0);
            if (_listItems.TryGetValue(nextId, out var item) && item.Tag is DownloadTask task)
            {
                AppLog.Write($"dequeue {task.FileName} → start");
                StartTask(task);
            }
        }
        UpdateToolbarButtons();
    }

    private void AddTaskRow(DownloadTask task)
    {
        var item = new ListViewItem(task.FileName) { Tag = task };
        item.SubItems.Add("-");
        item.SubItems.Add("0%");
        item.SubItems.Add("-");
        item.SubItems.Add("等待中");
        item.SubItems.Add(task.Url);
        _taskList.Items.Add(item);
        _listItems[task.Id] = item;
        UpdateTaskRow(task);
    }

    private void StartTask(DownloadTask task)
    {
        if (_runningTasks.ContainsKey(task.Id) || task.Status == DownloadStatus.Completed)
        {
            return;
        }
        _runningTasks[task.Id] = RunTaskAsync(task);
    }

    private static readonly int[] RetryDelaysMs = [2000, 5000, 10000];

    private async Task RunTaskAsync(DownloadTask task)
    {
        var cts = new CancellationTokenSource();
        _cancellations[task.Id] = cts;

        // 续传被跳过时，用户在进度回调里第一次看到原因；只播报一次，避免刷屏
        bool resumeNoteAnnounced = false;
        var progress = new Progress<DownloadProgress>(p =>
        {
            task.ReceivedBytes = p.ReceivedBytes;
            task.TotalBytes = p.TotalBytes;
            task.SpeedBytesPerSec = p.SpeedBytesPerSec;
            if (!resumeNoteAnnounced && task.ResumeNote != null)
            {
                resumeNoteAnnounced = true;
                _statusText.Text = $"正在下载: {task.FileName}（{task.ResumeNote}）";
            }
            UpdateTaskRow(task);
        });

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        task.Status = DownloadStatus.Retrying;
                        task.ErrorMessage = $"{RetryDelaysMs[attempt - 1] / 1000} 秒后自动重试 ({attempt}/{RetryDelaysMs.Length})";
                        UpdateTaskRow(task);
                        _statusText.Text = $"下载失败，重试中: {task.FileName}";
                        await Task.Delay(RetryDelaysMs[attempt - 1], cts.Token);
                    }

                    task.Status = DownloadStatus.Waiting;
                    UpdateTaskRow(task);

                    // 弹窗里已探测过的任务复用那份信息：不再重复发请求，
                    // 也不让服务器返回的文件名覆盖用户确认过的名字
                    if (!task.Probed)
                    {
                        _statusText.Text = $"正在获取文件信息: {task.Url}";

                        // 引擎的文件操作（预分配、状态读写）放后台线程，避免磁盘繁忙时卡住界面
                        // 带头探测：重启加载的任务可能带着持久化的 Referer，
                        // 漏传会让防盗链站点在探测阶段就被 403 打掉
                        var info = await Task.Run(
                            () => _downloadService.ProbeAsync(task.Url, cts.Token, task.RequestHeaders), cts.Token);
                        task.TotalBytes = info.TotalBytes;
                        task.AcceptRanges = info.AcceptRanges;
                        task.ETag = info.ETag;
                        task.LastModified = info.LastModified;

                        // 一旦开始下载（有进度、状态文件或预分配文件），改名会让旧路径上的
                        // 断点状态和半成品文件全部失效，因此只对从未下载过的任务接受改名
                        bool hasDownloadedData = task.ReceivedBytes > 0
                            || File.Exists(Utils.StatePathFor(task))
                            || File.Exists(task.FilePath);
                        if (!string.IsNullOrEmpty(info.FileName) && info.FileName != task.FileName && !hasDownloadedData)
                        {
                            task.FileName = Utils.GetUniqueFileName(task.SaveDir, info.FileName);
                        }
                    }

                    task.Status = DownloadStatus.Downloading;
                    UpdateTaskRow(task);
                    _statusText.Text = $"正在下载: {task.FileName}";

                    await Task.Run(() => _downloadService.DownloadAsync(task, progress, cts.Token), cts.Token);

                    if (!string.IsNullOrEmpty(task.ExpectedSha256))
                    {
                        _statusText.Text = $"正在校验 SHA256: {task.FileName}";
                        string actual = await Task.Run(() => Utils.ComputeSha256(task.FilePath), cts.Token);
                        task.ActualSha256 = actual;
                        if (!string.Equals(actual, task.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Sha256MismatchException(
                                $"SHA256 校验失败: 期望 {task.ExpectedSha256[..Math.Min(16, task.ExpectedSha256.Length)]}… 实际 {actual[..16]}…");
                        }
                        AppLog.Write($"task {task.FileName} sha256 verified: {actual[..Math.Min(16, actual.Length)]}…");
                    }

                    task.Status = DownloadStatus.Completed;
                    task.ReceivedBytes = task.TotalBytes;
                    task.ErrorMessage = null;
                    task.ResumeNote = null;
                    _statusText.Text = $"下载完成: {task.FilePath}";
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                    && attempt < RetryDelaysMs.Length
                    && IsTransientError(ex))
                {
                    task.ErrorMessage = ex.Message;
                    AppLog.Write($"task {task.FileName} failed (attempt {attempt + 1}), will retry: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (task.DeleteRequested)
            {
                Utils.TryDeleteFile(task.FilePath);
                Utils.TryDeleteFile(Utils.StatePathFor(task));
                AppLog.Write($"delete: \"{task.FileName}\" file + dtask removed after unwind");
            }
            else if (!task.RestartRequested)
            {
                task.Status = DownloadStatus.Paused;
                _statusText.Text = $"已暂停: {task.FileName}";
            }
            // RestartRequested：覆盖策略打回重下，状态由 RestartForOverwrite 重置，
            // 这里不显示"已暂停"，等 finally 里重启
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Error;
            task.ErrorMessage = Utils.DescribeHttpError(ex);
            _statusText.Text = $"下载失败: {task.FileName} - {task.ErrorMessage}";
            LogError(task, ex);
        }
        finally
        {
            _cancellations.Remove(task.Id);
            _runningTasks.Remove(task.Id);
            cts.Dispose();
            if (task.RestartRequested)
            {
                // 旧下载已收尾（句柄释放、状态文件落定），此时打回重下才不会撞车
                RestartForOverwrite(task);
            }
            AppLog.Write($"task {task.FileName} ended with status={task.Status}");
            UpdateTaskRow(task);
            UpdateToolbarButtons();
            SaveTaskList();
            StartNextQueued();
        }
    }

    private static bool IsTransientError(Exception ex)
    {
        if (ex is Sha256MismatchException)
        {
            return false;
        }
        if (ex is HttpRequestException h && h.StatusCode.HasValue
            && (int)h.StatusCode.Value is >= 400 and < 500)
        {
            return false;
        }
        return true;
    }

    private static void LogError(DownloadTask task, Exception ex)
    {
        AppLog.Write($"task {task.FileName} failed: {ex.GetType().Name}: {ex.Message}");
        try
        {
            Directory.CreateDirectory(AppDataPaths.LogDir);
            string entry =
                $"[{DateTime.Now:HH:mm:ss}] task={task.FilePath} url={task.Url} status={task.Status}\n" +
                $"{ex}\n\n";
            File.AppendAllText(Path.Combine(AppDataPaths.LogDir, $"error-{DateTime.Now:yyyyMMdd}.log"), entry);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private void OnStartClick(object? sender, EventArgs e)
    {
        foreach (ListViewItem item in _taskList.SelectedItems)
        {
            if (item.Tag is DownloadTask task
                && task.Status is DownloadStatus.Paused or DownloadStatus.Error)
            {
                EnqueueTask(task);
            }
        }
    }

    private void OnPauseClick(object? sender, EventArgs e)
    {
        bool any = false;
        foreach (ListViewItem item in _taskList.SelectedItems)
        {
            if (item.Tag is not DownloadTask task)
            {
                continue;
            }

            if (task.Status == DownloadStatus.Waiting && _pendingQueue.Contains(task.Id))
            {
                // 还在排队、尚未开始，直接移出队列即可
                _pendingQueue.Remove(task.Id);
                task.Status = DownloadStatus.Paused;
                UpdateTaskRow(task);
                any = true;
            }
            else if (task.Status is DownloadStatus.Downloading
                && _cancellations.TryGetValue(task.Id, out var cts))
            {
                task.Status = DownloadStatus.Paused;
                cts.Cancel();
                UpdateTaskRow(task);
                any = true;
            }
        }
        if (any)
        {
            _statusText.Text = "已暂停所选任务";
        }
    }

    private void OnDeleteClick(object? sender, EventArgs e)
    {
        foreach (ListViewItem item in _taskList.SelectedItems)
        {
            if (item.Tag is not DownloadTask task)
            {
                continue;
            }

            if (_runningTasks.ContainsKey(task.Id))
            {
                task.DeleteRequested = true;
                if (_cancellations.TryGetValue(task.Id, out var cts))
                {
                    cts.Cancel();
                }
                AppLog.Write($"delete requested: \"{task.FileName}\" (running, cleanup after unwind)");
            }
            else if (task.Status != DownloadStatus.Completed)
            {
                Utils.TryDeleteFile(task.FilePath);
                Utils.TryDeleteFile(Utils.StatePathFor(task));
                AppLog.Write($"deleted: \"{task.FileName}\" (file + dtask removed)");
            }
            else
            {
                AppLog.Write($"deleted: \"{task.FileName}\" (completed, downloaded file kept)");
            }

            _cancellations.Remove(task.Id);
            _pendingQueue.Remove(task.Id);
            _listItems.Remove(task.Id);
            _taskList.Items.Remove(item);
        }
        UpdateToolbarButtons();
        SaveTaskList();
        _statusText.Text = "已删除所选任务";
    }

    // 相同文本不写回，避免无谓重绘；高频速度刷新时能跳过大部分 UI 更新
    private static void SetText(ListViewItem.ListViewSubItem sub, string text)
    {
        if (sub.Text != text)
        {
            sub.Text = text;
        }
    }

    private void UpdateTaskRow(DownloadTask task)
    {
        if (!_listItems.TryGetValue(task.Id, out var item))
        {
            return;
        }

        SetText(item.SubItems[0], task.FileName);
        SetText(item.SubItems[1], Utils.FormatBytes(task.TotalBytes));
        SetText(item.SubItems[2], task.TotalBytes > 0
            ? $"{task.ReceivedBytes * 100 / task.TotalBytes}%"
            : "-");
        SetText(item.SubItems[3], task.Status == DownloadStatus.Downloading
            ? Utils.FormatSpeed(task.SpeedBytesPerSec)
            : "-");
        string statusText = task.Status switch
        {
            DownloadStatus.Waiting => _pendingQueue.Contains(task.Id) ? "排队中" : "等待中",
            DownloadStatus.Downloading => "下载中",
            DownloadStatus.Retrying => task.ErrorMessage ?? "重试中",
            DownloadStatus.Paused => "已暂停",
            DownloadStatus.Completed => "已完成",
            DownloadStatus.Error => task.ErrorMessage ?? "错误",
            _ => "-",
        };
        SetText(item.SubItems[4], statusText);
        string tip = task.Url;
        string? detail = task.Status == DownloadStatus.Error ? task.ErrorMessage : task.ResumeNote;
        if (detail != null)
        {
            tip = $"{task.Url}\n{detail}";
        }
        if (task.Referer is { } referer)
        {
            tip += $"\nReferer: {referer}";
        }
        item.ToolTipText = tip;

        // 选中项的状态变了要同步刷新工具栏，否则按钮停留在上一次选中时的灰亮状态
        if (_taskList.SelectedItems.Contains(item))
        {
            UpdateToolbarButtons();
        }

        if (_taskList.SelectedItems.Count > 0 && _taskList.SelectedItems[0] == item)
        {
            UpdateDetailLabels(task);
        }
    }

    private void UpdateDetailLabels(DownloadTask task)
    {
        _lblUrl.Text = $"URL: {task.Url}";
        _lblSavePath.Text = $"保存路径: {task.FilePath}";
        _lblProgress.Text = $"进度: {Utils.FormatBytes(task.ReceivedBytes)} / {Utils.FormatBytes(task.TotalBytes)}";
        string? sha = task.ActualSha256 ?? task.ExpectedSha256;
        _lblSha256.Text = sha != null
            ? $"SHA256{(task.ActualSha256 == null ? "(期望)" : "")}: {sha}"
            : "SHA256: -";
        _lblResume.Text = task.ResumeNote == null ? "" : $"续传: {task.ResumeNote}";
        _lblResume.Visible = task.ResumeNote != null;
        // 下载中的文件是半成品，算出的哈希无意义；文件被删后也无从计算
        _btnComputeSha.Enabled = task.Status == DownloadStatus.Completed && File.Exists(task.FilePath);
    }

    private void UpdateToolbarButtons()
    {
        var selected = GetSelectedTasks();

        _btnStart.Enabled = selected.Any(t => t.Status is DownloadStatus.Paused or DownloadStatus.Error);
        _btnPause.Enabled = selected.Any(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Waiting);
        _btnDelete.Enabled = selected.Count > 0;
    }

    private void SaveTaskList(bool synchronous = false)
    {
        // 快照必须在 UI 线程上构建：后台保存时若 UI 同时增删条目，遍历 _listItems 会抛异常
        var records = _listItems.Values
            .Select(i => i.Tag)
            .OfType<DownloadTask>()
            .Select(t => new TaskRecord(t.Url, t.SaveDir, t.FileName, t.TotalBytes, t.Status.ToString(), t.ExpectedSha256, t.Referer))
            .ToList();
        if (synchronous)
        {
            TaskStore.Save(records);
        }
        else
        {
            Task.Run(() => TaskStore.Save(records));
        }
    }

    private void OnTaskSelectionChanged(object? sender, EventArgs e)
    {
        UpdateToolbarButtons();

        if (_taskList.SelectedItems.Count > 0 && _taskList.SelectedItems[0].Tag is DownloadTask task)
        {
            UpdateDetailLabels(task);
        }
        else
        {
            _lblUrl.Text = "URL: -";
            _lblSavePath.Text = "保存路径: -";
            _lblProgress.Text = "进度: -";
            _lblSha256.Text = "SHA256: -";
            _lblResume.Text = "";
            _lblResume.Visible = false;
            _btnComputeSha.Enabled = false;
        }
    }
}
