using System.Diagnostics;

namespace Downloader;

public class SettingsDialog : Form
{
    private readonly NumericUpDown _numConcurrent;
    private readonly NumericUpDown _numSegments;
    private readonly NumericUpDown _numSpeedLimit;
    private readonly TextBox _txtSaveDir;
    private readonly CheckBox _chkDebugLog;
    private readonly CheckBox _chkSkipResumeCheck;
    private readonly CheckBox _chkStartEarly;
    private readonly ComboBox _cmbDuplicate;
    private readonly CheckBox _chkBrowser;
    private readonly Label _lblBrowserStatus;

    public int MaxConcurrent => (int)_numConcurrent.Value;
    public int Segments => (int)_numSegments.Value;
    public string SaveDir => _txtSaveDir.Text.Trim();
    public bool DebugLog => _chkDebugLog.Checked;
    public bool SkipResumeCheck => _chkSkipResumeCheck.Checked;
    public bool StartDownloadWhileDialogOpen => _chkStartEarly.Checked;
    public long SpeedLimitBytesPerSec => (long)_numSpeedLimit.Value * 1024 * 1024;
    public DuplicateLinkPolicy DuplicateLink => s_duplicatePolicies[_cmbDuplicate.SelectedIndex].Policy;

    // 接管状态不存设置：每次打开都从注册表与磁盘实测，initial 只用来判断用户是否改了主意
    public bool BrowserWanted => _chkBrowser.Checked;
    public bool BrowserWasOn { get; }

    private const string ChromeExtensionsUrl = "chrome://extensions";

    // 下拉框项与策略一一对应，靠下标互查
    static readonly (DuplicateLinkPolicy Policy, string Name)[] s_duplicatePolicies =
    [
        (DuplicateLinkPolicy.Ask, "每次询问"),
        (DuplicateLinkPolicy.NewCopy, "添加编号副本"),
        (DuplicateLinkPolicy.Overwrite, "重新下载已有文件"),
        (DuplicateLinkPolicy.ResumeOrComplete, "续传或完成已有任务"),
        (DuplicateLinkPolicy.Skip, "跳过不添加"),
    ];

    public SettingsDialog(AppSettings current)
    {
        Text = "设置";
        ClientSize = new Size(536, 588);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        // 布局网格：标签轴 x=14，内容轴 x=160，注释与所属控件的文字起点对齐，长注释限宽 350 自动折行
        Label SectionHeader(string text, int y) => new()
        {
            Text = text,
            Location = new Point(14, y),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = SystemColors.GrayText,
        };
        Panel Separator(int y) => new()
        {
            Location = new Point(14, y),
            Size = new Size(508, 1),
            BackColor = SystemColors.ControlLight,
        };
        Label Hint(string text, int x, int y, bool wrap = false) => new()
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = wrap ? new Size(350, 0) : Size.Empty,
        };

        var hdrDownload = SectionHeader("下载", 12);

        var lblConcurrent = new Label { Text = "最大并发任务数:", Location = new Point(14, 42), AutoSize = true };
        _numConcurrent = new NumericUpDown
        {
            Location = new Point(160, 38),
            Width = 80,
            Minimum = 1,
            Maximum = 16,
            Value = Math.Clamp(current.MaxConcurrentDownloads, 1, 16),
        };

        var lblSegments = new Label { Text = "每文件分片数:", Location = new Point(14, 72), AutoSize = true };
        _numSegments = new NumericUpDown
        {
            Location = new Point(160, 68),
            Width = 80,
            Minimum = 1,
            Maximum = 32,
            Value = Math.Clamp(current.SegmentsPerFile, 1, 32),
        };

        var lblSpeedLimit = new Label { Text = "全局限速 (MB/s):", Location = new Point(14, 102), AutoSize = true };
        _numSpeedLimit = new NumericUpDown
        {
            Location = new Point(160, 98),
            Width = 80,
            Minimum = 0,
            Maximum = 1024,
            Value = Math.Clamp(current.SpeedLimitBytesPerSec / 1024 / 1024, 0, 1024),
        };
        var lblSpeedHint = Hint("0 = 不限速，对所有任务生效", 248, 102);

        var lblDir = new Label { Text = "保存目录:", Location = new Point(14, 132), AutoSize = true };
        _txtSaveDir = new TextBox
        {
            Location = new Point(160, 128),
            Width = 296,
            Text = current.DefaultSaveDir,
            PlaceholderText = "留空则每次新建下载时手动选择",
        };
        var btnBrowse = new Button { Text = "浏览...", Location = new Point(462, 127), Size = new Size(66, 25) };
        btnBrowse.Click += (_, _) =>
        {
            using var folder = new FolderBrowserDialog();
            if (folder.ShowDialog(this) == DialogResult.OK)
            {
                _txtSaveDir.Text = folder.SelectedPath;
            }
        };

        var sepBehavior = Separator(162);
        var hdrBehavior = SectionHeader("行为", 172);

        var lblDuplicate = new Label { Text = "重复链接时:", Location = new Point(14, 202), AutoSize = true };
        _cmbDuplicate = new ComboBox
        {
            Location = new Point(160, 198),
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbDuplicate.Items.AddRange(s_duplicatePolicies.Select(p => p.Name).ToArray());
        int policyIndex = Array.FindIndex(s_duplicatePolicies, p => p.Policy == current.DuplicateLinkPolicy);
        _cmbDuplicate.SelectedIndex = policyIndex < 0 ? 0 : policyIndex;
        var lblDuplicateHint = Hint("添加任务时发现列表里已有同一链接的处理方式", 160, 224);

        _chkStartEarly = new CheckBox
        {
            Text = "弹窗显示期间即开始下载",
            Location = new Point(160, 248),
            AutoSize = true,
            Checked = current.StartDownloadWhileDialogOpen,
        };
        var lblStartEarlyHint = Hint("不等点「立即下载」；关闭弹窗（×/Esc）即取消，未确认的下载会被删除", 178, 270, wrap: true);

        var sepAdvanced = Separator(310);
        var hdrAdvanced = SectionHeader("高级", 320);

        _chkDebugLog = new CheckBox
        {
            Text = "记录调试日志",
            Location = new Point(160, 344),
            AutoSize = true,
            Checked = current.EnableDebugLog,
        };
        var lblDebugHint = Hint("排查问题时开启，会产生 trace.log", 178, 366);

        _chkSkipResumeCheck = new CheckBox
        {
            Text = "跳过续传前文件变化校验",
            Location = new Point(160, 388),
            AutoSize = true,
            Checked = current.SkipResumeIdentityCheck,
        };
        var lblSkipHint = Hint("（ETag / Last-Modified）服务器上的文件确实变过时仍会续传出新旧混合的数据；地址、大小、分片布局照常校验", 178, 410, wrap: true);

        var sepBrowser = Separator(452);
        var hdrBrowser = SectionHeader("下载接管", 462);

        BrowserIntegration.Snapshot browser = BrowserIntegration.Probe();
        BrowserWasOn = browser.Status is BrowserIntegration.State.Registered or BrowserIntegration.State.Incomplete;

        _chkBrowser = new CheckBox
        {
            Text = "启用 Chrome 下载接管",
            Location = new Point(160, 486),
            AutoSize = true,
            Checked = BrowserWasOn,
        };
        var btnOpenChrome = new Button
        {
            Text = "打开 Chrome",
            Location = new Point(344, 484),
            Size = new Size(160, 26),
        };
        new ToolTip().SetToolTip(btnOpenChrome, $"唤起 Chrome；扩展页 {ChromeExtensionsUrl} 属内部页面，需自己输入");
        btnOpenChrome.Click += (_, _) =>
        {
            // 撤走注册表条目后 Chrome 会自行移除该扩展，这里只用来把 Chrome 唤到前台，方便确认现状。
            // 注意：Chrome 会忽略命令行里的 chrome:// 内部地址（实测只开出新标签页），跳不了扩展页
            try
            {
                Process.Start(new ProcessStartInfo("chrome.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"无法启动 Chrome：{ex.Message}", "下载接管", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _lblBrowserStatus = new Label
        {
            Text = browser.Status == BrowserIntegration.State.OwnedElsewhere
                ? browser.Detail + "；勾选并确定将改由本程序接管"
                : browser.Detail,
            Location = new Point(178, 512),
            MaximumSize = new Size(350, 0),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(377, 552), Size = new Size(75, 28) };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(458, 552), Size = new Size(70, 28) };
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            hdrDownload,
            lblConcurrent, _numConcurrent, lblSegments, _numSegments,
            lblSpeedLimit, _numSpeedLimit, lblSpeedHint,
            lblDir, _txtSaveDir, btnBrowse,
            sepBehavior, hdrBehavior,
            lblDuplicate, _cmbDuplicate, lblDuplicateHint,
            _chkStartEarly, lblStartEarlyHint,
            sepAdvanced, hdrAdvanced,
            _chkDebugLog, lblDebugHint, _chkSkipResumeCheck, lblSkipHint,
            sepBrowser, hdrBrowser,
            _chkBrowser, _lblBrowserStatus, btnOpenChrome,
            btnOk, btnCancel,
        });
    }
}
