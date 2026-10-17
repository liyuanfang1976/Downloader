using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Downloader;

// 迅雷11「添加链接」弹窗：无边框窗体 + GDI+ 自绘。
// 粘贴链接（每行一条）后逐条调用注入的探测委托，回填文件名/类型/大小，
// 确认后通过 ConfirmedTasks 交回 MainForm 建任务；探测委托复用 DownloadService.ProbeAsync，
// 因此弹窗里看到的文件名/大小与真正下载时引擎拿到的是同一份信息，不会重复发请求
public class AddTaskDialog : Form
{
    const int DesignWidth = 600;
    const int DesignHeight = 519;
    const float RowHeight = 28f;
    const float ButtonRadius = 6f;
    // 文件名列的宽度上限：RowName 的截断和失败行图标的站位都按它算，避免两处各写一个数
    const float NameColumnWidth = 330f;

    static readonly Color Accent = Color.FromArgb(63, 133, 255);
    static readonly Color AccentHover = Color.FromArgb(96, 152, 255);
    static readonly Color AccentPressed = Color.FromArgb(45, 110, 230);
    static readonly Color AccentDisabled = Color.FromArgb(190, 213, 254);
    static readonly Color TextDark = Color.FromArgb(33, 36, 41);
    static readonly Color TextGray = Color.FromArgb(137, 142, 151);
    static readonly Color TextLight = Color.FromArgb(194, 198, 203);
    static readonly Color IconGray = Color.FromArgb(156, 160, 168);
    static readonly Color BorderGray = Color.FromArgb(228, 229, 231);
    static readonly Color BorderBlue = Color.FromArgb(63, 133, 255);
    static readonly Color PathBorder = Color.FromArgb(239, 240, 241);
    static readonly Color HoverGray = Color.FromArgb(240, 241, 243);
    static readonly Color CheckBorder = Color.FromArgb(201, 205, 212);
    static readonly Color ErrorRed = Color.FromArgb(228, 76, 61);

    enum RowState { Probing, Ready, Failed }

    // internal：边下边弹窗的抢跑委托要把行状态交给主程序，不能是 private
    sealed class FileRow
    {
        public required string Url { get; init; }
        public string FileName = "";
        public long TotalBytes = -1;
        public RemoteFileInfo? Info;
        public RowState State = RowState.Probing;
        public string? Error;
        // 探测失败后用户补填的引用页：用于重探，并随 ConfirmedTask 流入建单
        public string? Referer;
        // 边下边弹窗抢跑时主程序建好的任务；非空即该行已在下载，锁定改名/目录
        public DownloadTask? StartedTask;
        // 失败带着 HTTP 状态码（403/401 等）时才值得提示"可以指定引用页"，
        // 连不上服务器时给这个提示只会误导
        public bool CanRetryWithReferer;
        public bool Checked = true;
        public CancellationTokenSource? ProbeToken;
        public string Ext => Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();
    }

    readonly List<FileRow> _rows = [];
    readonly Func<string, Dictionary<string, string>?, CancellationToken, Task<RemoteFileInfo>> _probe;
    readonly Dictionary<string, string>? _headers;
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 500 };
    bool _applyingSize;
    float _formHeight;
    bool ShowList => _rows.Count > 0;
    string _saveDir;
    string _freeText = "";
    long _freeBytes;
    int _scrollOffset;

    public sealed record ConfirmedTask(string Url, string FileName, RemoteFileInfo Info, string? Referer = null, DownloadTask? Started = null);
    // 探测就绪时交给主程序的最小信息集：抢跑建单需要的都在这，
    // 不把弹窗内部的行类型暴露出去
    public sealed record ReadyRow(string Url, string FileName, RemoteFileInfo? Info, string? Referer);
    public List<ConfirmedTask> ConfirmedTasks { get; } = [];
    public string SaveDir => _saveDir;
    // 保存目录已选好且存在：提交门与目录行着色共用；抢跑侧拿到的是目录字符串，
    // 在 TryAutoStart 里判同一条规则
    bool HasSaveDir => _saveDir.Length > 0 && Directory.Exists(_saveDir);

    // 某行探测就绪（含失败行补救成功）时回调：主程序按边下边弹窗的设置决定是否
    // 抢跑建单，返回建好的任务或 null；弹窗只把结果记在行上用于锁定编辑
    public Func<ReadyRow, DownloadTask?>? OnRowReady { get; set; }
    // 勾掉某个已开始的行：主程序负责中止并删除那个未确认的任务
    public Action<DownloadTask>? OnTaskAbort { get; set; }

    // 任何一行开跑后保存目录即锁定：否则一批里前几个在旧目录、后几个在新目录
    bool DirLocked => _rows.Exists(r => r.StartedTask != null);

    PlaceholderTextBox _urlBox = null!;
    TextBox? _renameBox;
    FileRow? _renamingRow;
    Action? _commitRename;

    Font _fontTitle = null!, _fontBody = null!, _fontButton = null!;

    enum Zone { None, Close, Pencil, Check, Browse, Chevron, Button }
    Zone _hot = Zone.None, _pressed = Zone.None;
    FileRow? _hotRow;

    float S => DeviceDpi / 96f;

    RectangleF _rClose, _rUrl, _rList, _rRows, _rHeaderName, _rHeaderType, _rHeaderSize,
        _rTotal, _rSaveLabel, _rPath, _rMonitor, _rComputer, _rDir, _rFree, _rChevron,
        _rBrowse, _rButton;
    float _titleBarHeight;

    public AddTaskDialog(
        Func<string, Dictionary<string, string>?, CancellationToken, Task<RemoteFileInfo>> probe,
        string initialSaveDir,
        string? prefilledUrls = null,
        Dictionary<string, string>? headers = null)
    {
        _probe = probe;
        _headers = headers;
        _saveDir = (initialSaveDir ?? "").Trim();

        // 无边框窗口仍需设置 Text：任务栏/Alt-Tab 标题，也影响辅助功能与截图工具识别
        Text = "添加链接";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        BackColor = Color.White;

        _fontTitle = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point, 0);
        _fontBody = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point, 0);
        _fontButton = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point, 0);

        _urlBox = new PlaceholderTextBox
        {
            BorderStyle = BorderStyle.None,
            Font = _fontBody,
            ForeColor = TextDark,
            Placeholder = "粘贴下载链接，每行一个",
            PlaceholderColor = TextLight,
        };
        _urlBox.GotFocus += (_, _) => Invalidate();
        _urlBox.LostFocus += (_, _) => Invalidate();
        _urlBox.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        Controls.Add(_urlBox);

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SyncRowsFromUrls();
        };

        HandleCreated += (_, _) =>
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        };

        if (!string.IsNullOrWhiteSpace(prefilledUrls))
        {
            _urlBox.Text = prefilledUrls;
            SyncRowsFromUrls();
        }

        UpdateFreeSpace();
        Size = new Size((int)(DesignWidth * S), (int)(DesignHeight * S));
        LayoutRects();
        Resize += (_, _) => LayoutRects();
    }

    // 保留 WS_CAPTION|WS_THICKFRAME 让 DWM 认定这是标准窗口，从而继承原生的
    // 打开/关闭动画、系统阴影和 Win11 圆角；非客户区由 WM_NCCALCSIZE 清零，
    // 视觉上仍是无边框自绘窗口（Chromium 同款方案，替代效果生硬的 CS_DROPSHADOW）
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            const int WS_CAPTION = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_SYSMENU = 0x00080000;
            cp.Style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var row in _rows)
            {
                row.ProbeToken?.Cancel();
            }
            _fontTitle.Dispose();
            _fontBody.Dispose();
            _fontButton.Dispose();
            _debounce.Dispose();
        }
        base.Dispose(disposing);
    }

    void LayoutRects()
    {
        float s = S;
        float W = DesignWidth * s;
        float left = 20 * s;

        _titleBarHeight = 44 * s;
        _rClose = new RectangleF(W - 36 * s, 8 * s, 28 * s, 28 * s);

        _rUrl = new RectangleF(left, 64 * s, W - 40 * s, 120 * s);
        _urlBox.SetBounds(
            (int)(_rUrl.X + 10 * s), (int)(_rUrl.Y + 12 * s),
            (int)(_rUrl.Width - 20 * s), (int)(_rUrl.Height - 24 * s));

        float listTop = 197 * s;
        float listHeight = ShowList ? 143f * s : 0f;
        float listBottom = listTop + listHeight;
        _rList = new RectangleF(left, listTop, W - 40 * s, listHeight);

        _rHeaderName = new RectangleF(left + 64 * s, listTop + 5 * s, 200 * s, 20 * s);
        _rHeaderType = new RectangleF(left + 425 * s, listTop + 5 * s, 60 * s, 20 * s);
        _rHeaderSize = new RectangleF(left + 467 * s, listTop + 5 * s, 80 * s, 20 * s);

        // 行区域：表头下方到“总”行上方
        _rRows = new RectangleF(left, listTop + 30 * s, W - 40 * s, Math.Max(0, listHeight - 30 * s - 32 * s));
        _rTotal = new RectangleF(left, listBottom - 32 * s, _rList.Width - 20 * s, 22 * s);

        _rSaveLabel = new RectangleF(left, listBottom + 12 * s, 200 * s, 22 * s);

        float pathTop = listBottom + 44 * s;
        _rPath = new RectangleF(left, pathTop, W - 40 * s, 39 * s);
        _rMonitor = new RectangleF(left + 13 * s, pathTop + 11 * s, 17 * s, 17 * s);
        _rComputer = new RectangleF(left + 38 * s, pathTop, 40 * s, 39 * s);
        _rDir = new RectangleF(left + 75 * s, pathTop, 220 * s, 39 * s);
        _rFree = new RectangleF(left + 320 * s, pathTop, 172 * s, 39 * s);
        _rChevron = new RectangleF(left + 506 * s, pathTop + 12 * s, 15 * s, 15 * s);
        _rBrowse = new RectangleF(left + 528 * s, pathTop + 10.5f * s, 18 * s, 18 * s);

        // 紧凑按钮：宽度随文字自适应（左右各 32px 留白），最小 150px 避免显得局促
        float btnTextWidth = TextRenderer.MeasureText("立即下载", _fontButton, Size.Empty,
            TextFormatFlags.NoPadding).Width * (96f / DeviceDpi);
        float btnWidth = Math.Max(150 * s, btnTextWidth + 64 * s);
        float btnHeight = 36 * s;
        _rButton = new RectangleF((W - btnWidth) / 2, pathTop + 39 * s + 32 * s, btnWidth, btnHeight);

        // 面板收起时整窗等高收缩，与迅雷行为一致。
        // 必须用外框 Size 而非 ClientSize：WS_THICKFRAME 让 WinForms 误算出 8px 非客户区，
        // 而 NCCALCSIZE 已清零，实际客户区等于窗口，用 ClientSize 会把窗口缩窄
        _formHeight = _rButton.Bottom + 21 * s;
        if (!_applyingSize && (Size.Height != (int)_formHeight || Size.Width != (int)(DesignWidth * s)))
        {
            _applyingSize = true;
            Size = new Size((int)(DesignWidth * s), (int)_formHeight);
            _applyingSize = false;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutRects();
        _urlBox.Focus();
    }

    // ================= 真实解析 =================

    void SyncRowsFromUrls()
    {
        var urls = _urlBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        // 只 Cancel 不 Dispose：请求还在飞行中时先释放 CTS，HttpClient 注销自己的
        // 令牌注册时会撞上 ObjectDisposedException；CTS 未取用 WaitHandle，不持有句柄
        foreach (var gone in _rows)
        {
            if (!urls.Contains(gone.Url))
            {
                gone.ProbeToken?.Cancel();
            }
        }
        _rows.RemoveAll(r => !urls.Contains(r.Url));
        foreach (var url in urls)
        {
            if (_rows.Exists(r => r.Url == url))
            {
                continue;
            }
            var row = new FileRow { Url = url, FileName = Utils.GetFileNameFromUrl(url) };
            _rows.Add(row);
            StartProbe(row);
        }
        _scrollOffset = 0;
        LayoutRects();
        Invalidate();
    }

    async void StartProbe(FileRow row)
    {
        var cts = new CancellationTokenSource();
        row.ProbeToken = cts;
        try
        {
            var info = await _probe(row.Url, HeadersWith(row.Referer), cts.Token);
            if (!_rows.Contains(row))
            {
                return; // 探测期间链接被删掉了
            }
            ApplyProbe(row, info);
            // 边下边弹窗：探测即就绪，主程序可抢跑建单；返回值只是供弹窗锁定编辑。
            // 取消勾选的行不抢跑——复选框就是用户的包含/排除决策
            row.StartedTask = row.Checked ? OnRowReady?.Invoke(new ReadyRow(row.Url, row.FileName, row.Info, row.Referer)) : null;
        }
        catch (Exception ex)
        {
            if (!_rows.Contains(row))
            {
                return;
            }
            row.State = RowState.Failed;
            row.CanRetryWithReferer = ex is HttpRequestException { StatusCode: not null };
            // 粘贴的链接不是合法 URI 时给一句人话；其余与主程序状态列共用同一份映射，
            // 避免两处对同一种失败说法不一
            row.Error = ex is UriFormatException ? "链接格式不正确" : Utils.DescribeHttpError(ex);
            // 探测失败只显示在弹窗行里，不落日志的话用户事后想不起当时是什么链接出的问题
            AppLog.Write($"probe failed: {row.Url} — {row.Error}");
        }
        if (IsHandleCreated)
        {
            BeginInvoke(() =>
            {
                LayoutRects();
                Invalidate();
            });
        }
    }

    // 行级 Referer 覆盖批级带头（浏览器接管带来的那套）；探测与失败行重探共用
    Dictionary<string, string>? HeadersWith(string? referer)
        => referer is { } r ? Utils.WithHeader(_headers, "Referer", r) : _headers;

    static void ApplyProbe(FileRow row, RemoteFileInfo info)
    {
        if (!string.IsNullOrEmpty(info.FileName))
        {
            row.FileName = Utils.SanitizeFileName(info.FileName);
        }
        row.TotalBytes = info.TotalBytes;
        row.Info = info;
        row.State = RowState.Ready;
        row.Error = null;
    }

    // 探测失败行的补救入口：指定 Referer 后重新探测，成功则行转为 Ready。
    // 没有它，需要引用页的链接在弹窗里永远提交不了（探测 403 → 行失败 → 按钮不可用），
    // 而这类链接恰恰是每任务 Referer 最经典的场景
    void PromptReferer(FileRow row)
    {
        using var dialog = new RefererPromptDialog(
            row.Url, row.Referer,
            (url, referer, ct) => _probe(url, HeadersWith(referer), ct));
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Info is not { } info)
        {
            AppLog.Write($"probe retry cancelled: {row.Url}");
            return;
        }

        row.Referer = dialog.Referer;
        ApplyProbe(row, info);
        row.StartedTask = row.Checked ? OnRowReady?.Invoke(new ReadyRow(row.Url, row.FileName, row.Info, row.Referer)) : null;
        AppLog.Write($"probe retry with referer: {row.Url} → ok (total={info.TotalBytes})");
        LayoutRects();
        Invalidate();
    }

    void UpdateFreeSpace()
    {
        if (_saveDir.Length == 0)
        {
            _freeBytes = -1;
            _freeText = "-";
            return;
        }
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_saveDir)) ?? _saveDir);
            _freeBytes = drive.AvailableFreeSpace;
            _freeText = Utils.FormatBytes(_freeBytes);
        }
        catch
        {
            _freeBytes = -1;
            _freeText = "-";
        }
    }

    bool CanSubmit()
        => HasSaveDir && _rows.Exists(r => r.Checked && r.State == RowState.Ready);

    void Submit()
    {
        ConfirmedTasks.Clear();
        // 一批里两条链接可能给出同名文件，而磁盘上要等下载开始才落盘，
        // 所以既比对磁盘也比对本次已分配过的名字
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows.Where(r => r.Checked && r.State == RowState.Ready && r.Info != null))
        {
            // Started 非空 = 边下边弹窗已提前建单，主程序确认时跳过创建
            ConfirmedTasks.Add(new ConfirmedTask(row.Url, UniqueFileName(_saveDir, row.FileName, taken), row.Info!, row.Referer, row.StartedTask));
        }
        if (ConfirmedTasks.Count > 0)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    static string UniqueFileName(string dir, string fileName, HashSet<string> taken)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int i = 1;
        while (File.Exists(Path.Combine(dir, fileName)) || !taken.Add(Path.Combine(dir, fileName)))
        {
            fileName = $"{baseName}({i}){ext}";
            i++;
        }
        return fileName;
    }

    // ================= 键盘 =================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_renameBox != null)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }
        if (keyData == Keys.Enter && !_urlBox.Focused && CanSubmit())
        {
            Submit();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x83;
        const int WM_NCHITTEST = 0x84;
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // 非客户区清零：客户区铺满整个窗口，边框由自绘内容取代
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)1) // HTCLIENT
            {
                long lp = m.LParam.ToInt64();
                var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var pt = PointToClient(screen);
                if (pt.Y < _titleBarHeight && !_rClose.Contains(pt))
                {
                    m.Result = (IntPtr)2; // HTCAPTION
                }
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ================= 命中测试 =================

    RectangleF RowRect(int index)
        => new(_rRows.X, _rRows.Y + (index - _scrollOffset) * RowHeight * S, _rRows.Width, RowHeight * S);

    RectangleF RowCheck(FileRow row) => RowVisibleRect(row, r => new RectangleF(r.X + 12 * S, r.Y + (r.Height - 16 * S) / 2, 16 * S, 16 * S));
    RectangleF RowIcon(FileRow row) => RowVisibleRect(row, r => new RectangleF(r.X + 36 * S, r.Y + (r.Height - 20 * S) / 2, 20 * S, 20 * S));
    RectangleF RowName(FileRow row) => RowVisibleRect(row, r =>
    {
        float w = TextRenderer.MeasureText(row.FileName, _fontBody).Width * (96f / DeviceDpi);
        return new RectangleF(r.X + 64 * S, r.Y + (r.Height - 20 * S) / 2, Math.Min(w + 8 * S, NameColumnWidth * S), 20 * S);
    });
    // 失败行的错误文案比文件名长，图标让到名称列右端，避免压在文字上（绘制与命中共用此矩形）
    RectangleF RowPencil(FileRow row) => RowVisibleRect(row, r => new RectangleF(
        row.State == RowState.Failed
            ? r.X + (64 + NameColumnWidth - 14) * S
            : RowName(row).Right + 2 * S,
        r.Y + (r.Height - 14 * S) / 2, 14 * S, 14 * S));
    RectangleF RowType(FileRow row) => RowVisibleRect(row, r => new RectangleF(r.X + 425 * S, r.Y + (r.Height - 20 * S) / 2, 60 * S, 20 * S));
    RectangleF RowSize(FileRow row) => RowVisibleRect(row, r => new RectangleF(r.X + 467 * S, r.Y + (r.Height - 20 * S) / 2, 80 * S, 20 * S));
    RectangleF RowChevron(FileRow row) => RowVisibleRect(row, r => new RectangleF(r.X + 542 * S, r.Y + (r.Height - 14 * S) / 2, 14 * S, 14 * S));

    RectangleF RowVisibleRect(FileRow row, Func<RectangleF, RectangleF> map)
    {
        int idx = _rows.IndexOf(row);
        return idx < 0 ? RectangleF.Empty : map(RowRect(idx));
    }

    (Zone zone, FileRow? row) HitTest(Point pt)
    {
        if (_rClose.Contains(pt)) return (Zone.Close, null);
        if (_renameBox != null) return (Zone.None, null);
        if (_rButton.Contains(pt)) return (Zone.Button, null);
        bool dirLocked = DirLocked;
        if (!dirLocked && _rBrowse.Contains(pt)) return (Zone.Browse, null);
        if (!dirLocked && _rChevron.Contains(pt)) return (Zone.Chevron, null);

        if (_rRows.Contains(new PointF(pt.X, pt.Y)))
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var rr = RowRect(i);
                if (!rr.Contains(pt))
                {
                    continue;
                }
                var row = _rows[i];
                if (RowCheck(row).Contains(pt)) return (Zone.Check, row);
                // 失败行整行都是"指定引用页重探"的点击区：只靠角落里一个小图标太难发现
                if (row.State == RowState.Failed) return (Zone.Pencil, row);
                if (row.StartedTask == null && RowPencil(row).Contains(pt)) return (Zone.Pencil, row);
                return (Zone.None, row);
            }
        }
        return (Zone.None, null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (z, row) = HitTest(e.Location);
        if (z != _hot || row != _hotRow)
        {
            _hot = z;
            _hotRow = row;
            Cursor = z == Zone.None ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot != Zone.None || _pressed != Zone.None)
        {
            _hot = Zone.None;
            _pressed = Zone.None;
            _hotRow = null;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // 能收到窗体级按下事件就说明点在编辑框之外：直接结束改名并保存，
        // 不必像原生那样要求点到弹窗外面（WinForms 点击窗体空白不会转移焦点）
        if (_renameBox != null)
        {
            _commitRename?.Invoke();
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            var (z, _) = HitTest(e.Location);
            if (z is Zone.Button or Zone.Check or Zone.Pencil or Zone.Browse or Zone.Chevron or Zone.Close)
            {
                _pressed = z;
                Invalidate();
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var (z, row) = HitTest(e.Location);
        var pressed = _pressed;
        _pressed = Zone.None;
        if (e.Button == MouseButtons.Left && z == pressed)
        {
            switch (z)
            {
                case Zone.Close:
                    DialogResult = DialogResult.Cancel;
                    Close();
                    break;
                case Zone.Check when row != null:
                    row.Checked = !row.Checked;
                    if (!row.Checked && row.StartedTask != null)
                    {
                        // 勾掉已开跑的行 = 放弃它（与关弹窗取消同一语义）。
                        // 先摘行上的引用再通知：否则重选这行会把已删除的单当成"已建"
                        var started = row.StartedTask;
                        row.StartedTask = null;
                        OnTaskAbort?.Invoke(started);
                    }
                    Invalidate();
                    break;
                case Zone.Pencil when row != null:
                    // 失败行的铅笔是"指定引用页重探"，正常行才是改名
                    if (row.State == RowState.Failed)
                    {
                        PromptReferer(row);
                    }
                    else
                    {
                        StartRename(row);
                    }
                    break;
                case Zone.Browse:
                case Zone.Chevron:
                    BrowseFolder();
                    break;
                case Zone.Button:
                    Submit();
                    break;
            }
        }
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int visible = Math.Max(1, (int)(_rRows.Height / (RowHeight * S)));
        int max = Math.Max(0, _rows.Count - visible);
        int next = Math.Clamp(_scrollOffset + (e.Delta < 0 ? 1 : -1), 0, max);
        if (next != _scrollOffset)
        {
            _scrollOffset = next;
            Invalidate();
        }
    }

    void StartRename(FileRow row)
    {
        // 已开跑的行不能改名：文件句柄按旧名开着，改名只会让任务和磁盘文件对不上
        if (_renameBox != null || row.StartedTask != null)
        {
            return;
        }
        var nameRect = RowName(row);
        var box = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Text = row.FileName,
            Font = _fontBody,
            ForeColor = TextDark,
        };
        _renameBox = box;
        _renamingRow = row;
        Controls.Add(box);
        box.SetBounds((int)nameRect.Left, (int)nameRect.Y - 1,
            (int)Math.Max(nameRect.Width + 18 * S, 160 * S), (int)(23 * S));
        box.BringToFront();
        box.Focus();
        box.SelectAll();

        void Commit()
        {
            if (_renameBox == null)
            {
                return;
            }
            string newName = box.Text.Trim();
            if (newName.Length > 0 && newName != row.FileName)
            {
                row.FileName = Utils.SanitizeFileName(newName);
            }
            Controls.Remove(box);
            box.Dispose();
            _renameBox = null;
            _renamingRow = null;
            _commitRename = null;
            LayoutRects();
            Invalidate();
        }
        _commitRename = Commit;
        box.KeyDown += (_, ev) =>
        {
            if (ev.KeyCode == Keys.Enter)
            {
                ev.SuppressKeyPress = true;
                Commit();
            }
            else if (ev.KeyCode == Keys.Escape)
            {
                box.Text = row.FileName;
                Commit();
            }
        };
        box.KeyPress += (_, ev) =>
        {
            if (ev.KeyChar == '\r')
            {
                ev.Handled = true;
                Commit();
            }
        };
        box.LostFocus += (_, _) => Commit();
    }

    void BrowseFolder()
    {
        if (DirLocked)
        {
            return; // 下载已开始，目录不可改（命中测试也已屏蔽这两个区域）
        }
        using var dialog = new FolderBrowserDialog();
        dialog.SelectedPath = _saveDir;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _saveDir = dialog.SelectedPath;
            UpdateFreeSpace();
            Invalidate();
        }
    }

    // ================= 绘制 =================

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = S;

        DrawTitleBar(g, s);
        DrawUrlBox(g, s);
        DrawFileList(g, s);
        DrawSaveRow(g, s);
        DrawButton(g, s);
    }

    void DrawTitleBar(Graphics g, float s)
    {
        // 标题按字形墨迹中心对齐到 × 图标的几何中心（标题栏 44px 的中线 22px）
        DrawTextVC(g, "添加链接", _fontTitle, new RectangleF(20 * s, 0, 300 * s, 44 * s), TextDark);

        if (_hot == Zone.Close || _pressed == Zone.Close)
        {
            using var path = RoundedRect(_rClose, 14 * s);
            using var bg = new SolidBrush(HoverGray);
            g.FillPath(bg, path);
        }
        // lucide x 的字形在 24 格制中左右上下均对称（6..18），所以把图标框居中即墨迹居中；
        // 17px 时墨迹约 9px，与参考图实测一致，中心落在 (W-22, 22) 与标题墨迹中心同轴
        float xSize = 17 * s;
        Lucide.Draw(g, "x", new PointF(_rClose.X + (28 * s - xSize) / 2, _rClose.Y + (28 * s - xSize) / 2),
            xSize, IconGray, 2.2f);
    }

    void DrawUrlBox(Graphics g, float s)
    {
        bool focused = _urlBox.Focused;
        using var bg = new SolidBrush(Color.White);
        using var path = RoundedRect(_rUrl, 8 * s);
        g.FillPath(bg, path);
        using var pen = new Pen(focused ? BorderBlue : BorderGray, 1f);
        g.DrawPath(pen, path);
    }

    void DrawFileList(Graphics g, float s)
    {
        if (!ShowList)
        {
            return; // 无链接时面板整体收起（与迅雷一致）
        }

        using var panelPath = RoundedRect(_rList, 8 * s);
        using var panelBorder = new Pen(BorderGray, 1f);
        g.DrawPath(panelBorder, panelPath);

        DrawTextVC(g, "文件名", _fontBody, _rHeaderName, TextGray);
        DrawTextVC(g, "类型", _fontBody, _rHeaderType, TextGray);
        DrawTextVC(g, "大小", _fontBody, _rHeaderSize, TextGray);

        var clip = g.Clip;
        g.SetClip(RectangleF.Inflate(_rRows, 0, 2));
        for (int i = 0; i < _rows.Count; i++)
        {
            var rr = RowRect(i);
            if (rr.Bottom < _rRows.Y || rr.Top > _rRows.Bottom)
            {
                continue;
            }
            DrawRow(g, s, _rows[i], rr);
        }
        g.Clip = clip;

        var ready = _rows.FindAll(r => r.Checked && r.State == RowState.Ready);
        long total = 0;
        foreach (var r in ready)
        {
            total += Math.Max(r.TotalBytes, 0);
        }
        string totalText = _rows.Count == 0
            ? ""
            : _rows.Exists(r => r.State == RowState.Probing)
                ? "解析中…"
                : $"总: {ready.Count}个文件({Utils.FormatBytes(total)})";
        DrawTextVC(g, totalText, _fontBody, _rTotal, TextGray, rightAlign: true);
    }

    void DrawRow(Graphics g, float s, FileRow row, RectangleF rr)
    {
        var checkRect = RowCheck(row);
        var iconRect = RowIcon(row);
        var nameRect = RowName(row);
        var pencilRect = RowPencil(row);
        var typeRect = RowType(row);
        var sizeRect = RowSize(row);
        var chevronRect = RowChevron(row);

        DrawCheckbox(g, s, checkRect, row.Checked);
        if (FileTypeBadge.ShowFor(row.Ext))
        {
            FileTypeBadge.Draw(g, iconRect, row.Ext);
        }

        // 正在改名的行由编辑框接管显示，原文字与铅笔都让位
        bool editing = _renameBox != null && _renamingRow == row;
        if (!editing)
        {
            bool failed = row.State == RowState.Failed;
            var nameColor = failed ? ErrorRed : row.Checked ? TextDark : TextLight;
            // 失败行的文字区止于图标前；错误原因自带操作提示——
            // 只写"服务器拒绝访问 (403)"的话，用户根本不知道这一行还能点
            var textRect = failed
                ? new RectangleF(nameRect.X, nameRect.Y, NameColumnWidth * s - 20 * s, nameRect.Height)
                : nameRect;
            string text = row.FileName;
            if (failed && row.Error != null)
            {
                text = row.CanRetryWithReferer ? $"{row.Error}，点击指定引用页重探" : row.Error;
            }
            DrawTextVC(g, Ellipsize(g, text, _fontBody, textRect.Width), _fontBody, textRect, nameColor);

            // 失败行的图标是"指定引用页重探"入口（link），正常行才是改名（pencil）；
            // 已开跑的行两个都不画——改名被锁，给了入口也是死的
            if (row.StartedTask == null)
            {
                Lucide.Draw(g, failed ? "link" : "pencil",
                    new PointF(pencilRect.X, pencilRect.Y), 13 * s,
                    _hot == Zone.Pencil && _hotRow == row ? TextDark : TextGray, 2.2f);
            }
        }

        DrawTextVC(g, row.Ext.Length > 0 ? row.Ext : "-", _fontBody, typeRect, TextGray, inkCenter: true);
        string sizeText = row.State switch
        {
            RowState.Probing => "解析中",
            RowState.Failed => "-",
            _ => row.TotalBytes > 0 ? Utils.FormatBytes(row.TotalBytes) : "未知",
        };
        DrawTextVC(g, sizeText, _fontBody, sizeRect, TextGray, inkCenter: true);
        Lucide.Draw(g, "chevron-down", new PointF(chevronRect.X, chevronRect.Y), 12 * s, TextLight, 2f);
    }

    void DrawCheckbox(Graphics g, float s, RectangleF r, bool isChecked)
    {
        using var path = RoundedRect(r, 4 * s);
        if (isChecked)
        {
            using var fill = new SolidBrush(Accent);
            g.FillPath(fill, path);
            // lucide check 路径占 24 格制中的 (4..20, 6..17)，按比例缩放后在框内精确居中
            float k = 10 * s / 16f;
            float checkW = (20 - 4) * k, checkH = (17 - 6) * k;
            float ox = r.X + (r.Width - checkW) / 2 - 4 * k;
            float oy = r.Y + (r.Height - checkH) / 2 - 6 * k;
            using var pen = new Pen(Color.White, 2.2f * s);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new PointF[]
            {
                new(ox + 20 * k, oy + 6 * k),
                new(ox + 9 * k, oy + 17 * k),
                new(ox + 4 * k, oy + 12 * k),
            });
        }
        else
        {
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
            using var pen = new Pen(CheckBorder, 1.2f);
            g.DrawPath(pen, path);
        }
    }

    void DrawSaveRow(Graphics g, float s)
    {
        DrawTextVC(g, "下载到:", _fontBody, _rSaveLabel, TextDark);

        using var path = RoundedRect(_rPath, 8 * s);
        using var white = new SolidBrush(Color.White);
        g.FillPath(white, path);
        using var border = new Pen(PathBorder, 1f);
        g.DrawPath(border, path);

        Lucide.Draw(g, "monitor", new PointF(_rMonitor.X, _rMonitor.Y), 17 * s, IconGray, 1.8f);
        DrawTextVC(g, "电脑", _fontBody, _rComputer, TextDark);
        string dirText = _saveDir.Length == 0 ? "点击右侧图标选择保存目录" : _saveDir;
        // 下载已开始时目录锁定：置灰 + 图标不再响应悬停，一眼看出改不了
        bool dirLocked = DirLocked;
        Color dirColor = dirLocked ? TextLight
            : _saveDir.Length == 0 ? TextLight
            : HasSaveDir ? TextDark : ErrorRed;
        DrawTextVC(g, Ellipsize(g, dirText, _fontBody, _rDir.Width), _fontBody, _rDir, dirColor);

        long selected = 0;
        foreach (var r in _rows.Where(r => r.Checked && r.State == RowState.Ready))
        {
            selected += Math.Max(r.TotalBytes, 0);
        }
        bool notEnough = _freeBytes >= 0 && selected > 0 && selected > _freeBytes;
        string freeText = dirLocked ? $"剩余:{_freeText} · 已锁定" : $"剩余:{_freeText}";
        DrawTextVC(g, freeText, _fontBody, _rFree, notEnough ? ErrorRed : TextLight, rightAlign: true);

        Lucide.Draw(g, "chevron-down", new PointF(_rChevron.X, _rChevron.Y), 15 * s,
            !dirLocked && _hot == Zone.Chevron ? TextDark : TextLight, 2f);
        Lucide.Draw(g, "folder-open", new PointF(_rBrowse.X, _rBrowse.Y), 18 * s,
            !dirLocked && _hot == Zone.Browse ? TextDark : IconGray, 2f);
    }

    void DrawButton(Graphics g, float s)
    {
        bool enabled = CanSubmit();
        var color = !enabled ? AccentDisabled
            : _pressed == Zone.Button ? AccentPressed
            : _hot == Zone.Button ? AccentHover
            : Accent;

        // 四角同半径，且半径须整除边长以免上下左右弧长不一致
        using var path = RoundedRect(_rButton, ButtonRadius * s);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);

        DrawTextVC(g, "立即下载", _fontButton, _rButton, Color.White, centerAlign: true);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static readonly Dictionary<string, (float Baseline, float Cap)> s_fontBody = [];

    // 布局框顶部→基线的距离、以及大写高度（cap height），按字体+字号缓存
    static (float Baseline, float Cap) FontBody(Font font, float dpiY)
    {
        float em = font.Size * dpiY / 72f;
        string key = font.Name + "/" + em.ToString("0.###");
        if (s_fontBody.TryGetValue(key, out var cached))
        {
            return cached;
        }
        using var probe = new GraphicsPath();
        probe.AddString("H", font.FontFamily, (int)font.Style, em, PointF.Empty, StringFormat.GenericTypographic);
        var box = probe.GetBounds();
        s_fontBody[key] = (box.Y + box.Height, box.Height);
        return s_fontBody[key];
    }

    // 两种垂直居中锚点：
    // capCenter（默认）按字体大写框居中，让同一段文字里的各字符共用基线；
    // inkCenter 按该字符串自身墨迹居中，用于表格里孤立的短词（exe、状态），
    // 它们与相邻列隔着一大段空白，眼睛比的是质量中心而不是基线
    static void DrawTextVC(Graphics g, string text, Font font, RectangleF r, Color color,
        bool rightAlign = false, bool centerAlign = false, bool inkCenter = false)
    {
        if (text.Length == 0)
        {
            return;
        }
        float anchor;
        if (inkCenter)
        {
            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size * g.DpiY / 72f,
                PointF.Empty, StringFormat.GenericTypographic);
            var ink = path.GetBounds();
            anchor = ink.Y + ink.Height / 2;
        }
        else
        {
            var (baseline, cap) = FontBody(font, g.DpiY);
            anchor = baseline - cap / 2;
        }
        int y = (int)(r.Y + r.Height / 2 - anchor);

        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        int x = centerAlign ? (int)(r.X + (r.Width - size.Width) / 2)
            : rightAlign ? (int)(r.Right - size.Width) : (int)r.X;
        TextRenderer.DrawText(g, text, font, new Point(x, y), color, TextFormatFlags.NoPadding);
    }

    static string Ellipsize(Graphics g, string text, Font font, float maxWidth)
    {
        if (text.Length == 0 || TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding).Width <= maxWidth)
        {
            return text;
        }
        while (text.Length > 0 && TextRenderer.MeasureText(g, text + "…", font, Size.Empty, TextFormatFlags.NoPadding).Width > maxWidth)
        {
            text = text[..^1];
        }
        return text + "…";
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
