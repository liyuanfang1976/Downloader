using System.Drawing;

namespace Downloader;

// 重复链接四策略（对应 IDM 资源 298）：新建的任务撞上列表里已有同一地址的任务时弹出，
// 让用户选怎么处理，并可记住选择。按地址匹配（忽略 query），与续传校验同口径
public class DuplicateLinkDialog : Form
{
    readonly RadioButton _radCopy;
    readonly RadioButton _radOverwrite;
    readonly RadioButton _radResume;
    readonly CheckBox _chkRemember;

    public DuplicateLinkPolicy Selected { get; private set; } = DuplicateLinkPolicy.Skip;
    public bool Remember => _chkRemember.Checked;

    public DuplicateLinkDialog(DownloadTask existing, string copyName)
    {
        Text = "链接已存在";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(480, 330);

        var lblTitle = new Label
        {
            Text = "列表中已存在该链接的任务：",
            Location = new Point(16, 14),
            AutoSize = true,
        };
        var lblName = new Label
        {
            Text = Ellipsis(existing.FileName, 42),
            Location = new Point(16, 34),
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
        };
        var lblState = new Label
        {
            Text = Ellipsis(Utils.DescribeState(existing), 44),
            Location = new Point(16, 56),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        _radCopy = new RadioButton
        {
            Text = $"添加为编号副本（{Ellipsis(copyName, 30)}）",
            Location = new Point(16, 84),
            AutoSize = true,
            Checked = true,
        };
        _radOverwrite = new RadioButton
        {
            Text = "重新下载已有文件（放弃当前进度）",
            Location = new Point(16, 110),
            AutoSize = true,
        };
        _radResume = new RadioButton
        {
            Text = ResumeText(existing),
            Location = new Point(16, 136),
            AutoSize = true,
        };
        var radSkip = new RadioButton
        {
            Text = "取消，不添加这个链接",
            Location = new Point(16, 162),
            AutoSize = true,
        };

        _chkRemember = new CheckBox
        {
            Text = "记住选择，以后不再问",
            Location = new Point(16, 194),
            AutoSize = true,
        };
        var lblHint = new Label
        {
            Text = "之后可在「设置」里更改",
            Location = new Point(34, 214),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(280, 248), Size = new Size(78, 28) };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(366, 248), Size = new Size(78, 28) };
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        btnOk.Click += (_, _) => Selected = _radCopy.Checked ? DuplicateLinkPolicy.NewCopy
            : _radOverwrite.Checked ? DuplicateLinkPolicy.Overwrite
            : _radResume.Checked ? DuplicateLinkPolicy.ResumeOrComplete
            : DuplicateLinkPolicy.Skip;

        Controls.AddRange(new Control[]
        {
            lblTitle, lblName, lblState,
            _radCopy, _radOverwrite, _radResume, radSkip,
            _chkRemember, lblHint, btnOk, btnCancel,
        });
    }

    // 第三个选项的文案随已有任务的状态变化，让用户点下去之前就知道会发生什么
    static string ResumeText(DownloadTask task)
    {
        if (task.Status == DownloadStatus.Downloading)
        {
            return "该任务正在下载中，直接沿用（不重复添加）";
        }
        if (task.Status == DownloadStatus.Completed)
        {
            return File.Exists(task.FilePath)
                ? "该任务已完成，直接沿用（不重复下载）"
                : "原文件已不存在，重新下载该任务";
        }
        return File.Exists(Utils.StatePathFor(task))
            ? $"续传该任务（当前 {task.ReceivedBytes * 100 / Math.Max(task.TotalBytes, 1)}%）"
            : "重新下载该任务（当前没有可用断点）";
    }

    static string Ellipsis(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
