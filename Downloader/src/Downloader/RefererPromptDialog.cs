using System.Drawing;

namespace Downloader;

// 探测失败行的补救入口：为这个链接指定 Referer 后重新探测。没有它，需要引用页的
// 链接在添加弹窗里根本提交不了（探测 403 → 行失败 → "立即下载"不可用），
// 而这类链接恰恰是每任务 Referer 最经典的场景
public class RefererPromptDialog : Form
{
    readonly string _url;
    readonly TextBox _txtReferer;
    readonly Label _lblResult;
    readonly Button _btnRetry;
    readonly Button _btnOk;
    readonly Func<string, string?, CancellationToken, Task<RemoteFileInfo>> _probe;
    readonly CancellationTokenSource _cts = new();

    public string? Referer { get; private set; }
    public RemoteFileInfo? Info { get; private set; }

    public RefererPromptDialog(
        string url,
        string? currentReferer,
        Func<string, string?, CancellationToken, Task<RemoteFileInfo>> probe)
    {
        _url = url;
        _probe = probe;
        Text = "指定引用页";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(540, 300);

        var lblHint = new Label
        {
            Text = "该链接的探测被拒绝，可能是服务器要求引用页（Referer）。\n填写来源页面的地址后重新探测：",
            Location = new Point(16, 14),
            AutoSize = true,
        };
        var lblUrl = new Label { Text = "地址:", Location = new Point(16, 60), AutoSize = true };
        var txtUrl = new TextBox
        {
            Text = url,
            Location = new Point(80, 57),
            Width = 424,
            ReadOnly = true,
            BackColor = SystemColors.Control,
        };
        var lblReferer = new Label { Text = "引用页:", Location = new Point(16, 92), AutoSize = true };
        _txtReferer = new TextBox
        {
            Text = currentReferer ?? "",
            Location = new Point(80, 89),
            Width = 424,
        };
        _lblResult = new Label
        {
            Location = new Point(80, 115),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        _btnRetry = new Button { Text = "重新探测", Location = new Point(80, 148), Size = new Size(96, 28) };
        _btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(330, 208), Size = new Size(78, 28), Enabled = false };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(416, 208), Size = new Size(78, 28) };
        AcceptButton = _btnOk;
        CancelButton = btnCancel;
        _btnRetry.Click += async (_, _) => await RetryAsync();

        Controls.AddRange(new Control[]
        {
            lblHint, lblUrl, txtUrl, lblReferer, _txtReferer, _lblResult,
            _btnRetry, _btnOk, btnCancel,
        });
    }

    async Task RetryAsync()
    {
        string referer = _txtReferer.Text.Trim();
        if (referer.Length == 0)
        {
            ShowResult("请先填写引用页地址", false);
            return;
        }
        if (!Utils.IsValidReferer(referer))
        {
            ShowResult("引用页必须是完整的 http/https 地址", false);
            return;
        }

        _btnRetry.Enabled = false;
        ShowResult("正在探测…", null);
        try
        {
            var info = await _probe(_url, referer, _cts.Token).ConfigureAwait(true);
            Referer = referer;
            Info = info;
            ShowResult("探测成功", true);
            _btnOk.Enabled = true;
        }
        catch (Exception ex)
        {
            // 探测失败原因直接展示：403 就还是 403，换个引用页再试
            ShowResult($"探测失败：{ex.Message}", false);
        }
        finally
        {
            _btnRetry.Enabled = true;
        }
    }

    void ShowResult(string text, bool? success)
    {
        _lblResult.ForeColor = success switch
        {
            true => Color.ForestGreen,
            false => Color.Firebrick,
            null => SystemColors.GrayText,
        };
        _lblResult.Text = text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 只 Cancel 不 Dispose：探测可能还在飞行中，提前释放 CTS 会让 HttpClient
            // 注销令牌注册时撞 ObjectDisposedException
            _cts.Cancel();
        }
        base.Dispose(disposing);
    }
}
