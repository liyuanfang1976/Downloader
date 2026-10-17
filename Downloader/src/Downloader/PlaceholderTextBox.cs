namespace Downloader;

// 多行 TextBox 的占位提示。
// EM_SETCUEBANNER 对 multiline 编辑框无效，WM_PAINT 后自绘又会被已验证的空 update region 裁掉，
// 所以只能叠一层 Label；但 Label 必须避开插入符所在的列（子控件的背景绘制会抹掉系统光标），
// 且它自带 2~3px 内部留白会让间距失控，故重画为贴边绘制
sealed class PlaceholderTextBox : TextBox
{
    // 文字从自身 (0,0) 起画，Left 即视觉起点
    sealed class EdgeLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Window);
            TextRenderer.DrawText(e.Graphics, Text, Font, Point.Empty, ForeColor,
                TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top);
        }
    }

    readonly EdgeLabel _label = new()
    {
        AutoSize = true,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        Location = new Point(4, 1),
    };

    public string Placeholder
    {
        get => _label.Text;
        init => _label.Text = value;
    }

    public Color PlaceholderColor
    {
        get => _label.ForeColor;
        init => _label.ForeColor = value;
    }

    public PlaceholderTextBox()
    {
        Multiline = true;
        Controls.Add(_label);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _label.Font = Font;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _label.Visible = TextLength == 0;
    }
}
