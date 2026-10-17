using System.Drawing;

namespace Downloader;

// 任务属性。目前唯一可改的是 Referer——它是唯一被持久化的请求头：
// 防盗链站点重启后续传/重下仍然带得上。Cookie/Authorization 等由浏览器接管时
// 自动带入内存，不写盘（tasks.json 是明文的），这个边界必须在界面上讲清楚
public class TaskPropertiesDialog : Form
{
    readonly TextBox _txtReferer;
    readonly Label _lblError;

    public string? Referer { get; private set; }

    public TaskPropertiesDialog(DownloadTask task)
    {
        Text = "任务属性";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(540, 310);

        var lblUrl = new Label { Text = "地址:", Location = new Point(16, 16), AutoSize = true };
        var txtUrl = new TextBox
        {
            Text = task.Url,
            Location = new Point(80, 13),
            Width = 424,
            ReadOnly = true,
            BackColor = SystemColors.Control,
        };

        var lblState = new Label
        {
            Text = $"状态: {Utils.DescribeState(task)}",
            Location = new Point(16, 46),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        var lblReferer = new Label { Text = "引用页:", Location = new Point(16, 78), AutoSize = true };
        _txtReferer = new TextBox
        {
            Text = task.Referer ?? "",
            Location = new Point(80, 75),
            Width = 424,
        };
        _lblError = new Label
        {
            Location = new Point(80, 101),
            AutoSize = true,
            ForeColor = Color.Firebrick,
        };

        var lblNote = new Label
        {
            Text = "Referer 会随任务保存，重启后仍然有效；留空表示不带（引擎会自动补同源根路径）。\n" +
                "Cookie / Authorization 等仅由浏览器接管时自动带入内存，不写入磁盘。",
            Location = new Point(16, 132),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        var lblHint = new Label
        {
            Text = "对正在下载的任务，改动在引擎发起下一次请求时生效。",
            Location = new Point(16, 176),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(330, 226), Size = new Size(78, 28) };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(416, 226), Size = new Size(78, 28) };
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        btnOk.Click += (_, _) =>
        {
            string value = _txtReferer.Text.Trim();
            if (value.Length == 0)
            {
                Referer = null;
                return;
            }
            if (!Utils.IsValidReferer(value))
            {
                _lblError.Text = "引用页必须是完整的 http/https 地址";
                return;
            }
            Referer = value;
        };

        Controls.AddRange(new Control[]
        {
            lblUrl, txtUrl, lblState,
            lblReferer, _txtReferer, _lblError,
            lblNote, lblHint, btnOk, btnCancel,
        });
    }
}
